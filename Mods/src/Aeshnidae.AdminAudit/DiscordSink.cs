namespace Aeshnidae.AdminAudit;

/// <summary>
/// Optional live feed of the audit trail into a private Discord channel.
///
/// Deliberately a second implementation rather than a call into Aeshnidae.DiscordRelay:
/// mods load into separate collectible assembly contexts, so calling across is awkward
/// and would couple the audit trail's availability to a chat relay's. The duplication
/// is the cheaper of the two costs. If a third consumer ever appears, extract this into
/// a shared source file that both projects <c>&lt;Compile Include&gt;</c>.
///
/// This sink is a convenience, never the record - if Discord is down, the JSONL file is
/// unaffected.
/// </summary>
internal sealed class DiscordSink : IDisposable
{
    private const int DiscordContentLimit = 1900;
    private const int MaxPostsPerFlush = 2;
    private const int MaxQueued = 500;

    private readonly DiscordSettings _settings;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private long _posted, _dropped;
    private volatile bool _stopping, _parked;
    private volatile string _lastError = "";

    public long Posted => Interlocked.Read(ref _posted);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string LastError => _lastError;
    public bool Parked => _parked;
    public int Pending => _queue.Count;

    /// <summary>The webhook this sink posts to; the routing table may create several.</summary>
    public string WebhookUrl { get; }

    /// <summary>Name for status output - the route prefix, or "default".</summary>
    public string Label { get; }

    public DiscordSink(DiscordSettings settings, string webhookUrl, string label = "default")
    {
        _settings = settings;
        WebhookUrl = webhookUrl;
        Label = label;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Aeshnidae-AdminAudit/1.0");

        _pump = Task.Run(PumpAsync);
    }

    public void Post(AuditEvent record)
    {
        if (_stopping || _parked)
            return;

        if (record.ReadOnly && !_settings.IncludeReadOnly)
            return;

        if (_queue.Count >= MaxQueued)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(Escape(record.ToLine()));
    }

    private async Task PumpAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.BatchSeconds, 1, 60));

        while (!_stopping)
        {
            try
            {
                await Task.Delay(interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(_cts.Token);
        }

        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(final.Token);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        if (_queue.IsEmpty || _parked)
            return;

        var posts = new List<string>();
        var sb = new StringBuilder();

        while (posts.Count < MaxPostsPerFlush && _queue.TryPeek(out var peeked))
        {
            var line = peeked.Length > DiscordContentLimit ? peeked[..(DiscordContentLimit - 1)] + "…" : peeked;

            if (sb.Length > 0 && sb.Length + 1 + line.Length > DiscordContentLimit)
            {
                posts.Add(sb.ToString());
                sb.Clear();

                if (posts.Count == MaxPostsPerFlush)
                    break;
            }

            _queue.TryDequeue(out _);

            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(line);
        }

        if (sb.Length > 0 && posts.Count < MaxPostsPerFlush)
            posts.Add(sb.ToString());

        foreach (var content in posts)
        {
            if (ct.IsCancellationRequested || !await PostAsync(content, ct))
                return;
        }
    }

    private async Task<bool> PostAsync(string content, CancellationToken ct)
    {
        var payload = new
        {
            content,
            username = string.IsNullOrWhiteSpace(_settings.Username)
                ? null
                : _settings.Username.Replace("discord", "disc0rd", StringComparison.OrdinalIgnoreCase),
            allowed_mentions = new { parse = Array.Empty<string>() },
        };

        var json = JsonSerializer.Serialize(payload);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(WebhookUrl, body, ct);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _posted);
                    return true;
                }

                if ((int)response.StatusCode == 429 && attempt == 0)
                {
                    var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 3;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30)), ct);
                    continue;
                }

                _lastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                if ((int)response.StatusCode is 401 or 403 or 404)
                {
                    _parked = true;
                    ModManager.Log($"[{Mod.Name}] audit webhook rejected with HTTP {(int)response.StatusCode}; " +
                                   "the Discord feed is parked until reload. The JSONL trail is unaffected.",
                                   ModManager.LogLevel.Error);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                _lastError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        return false;
    }

    /// <summary>Stops player- or admin-supplied text reformatting the audit channel.</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);

        foreach (var c in text)
        {
            if (c is '\r' or '\n' or '\t')
            {
                sb.Append(' ');
                continue;
            }

            if (char.IsControl(c))
                continue;

            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']')
                sb.Append('\\');

            sb.Append(c);
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _cts.Cancel();
            _pump.Wait(TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] audit Discord feed did not stop cleanly: {ex.Message}", ModManager.LogLevel.Warn);
        }
        finally
        {
            _http.Dispose();
            _cts.Dispose();
        }
    }
}
