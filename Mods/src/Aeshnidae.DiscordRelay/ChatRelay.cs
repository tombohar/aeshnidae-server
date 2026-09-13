namespace Aeshnidae.DiscordRelay;

/// <summary>
/// The bridge itself: an in-memory queue per Discord destination, drained by one
/// background task that batches whatever accumulated into a single webhook POST.
///
/// Two constraints shape this.
///
/// First, <see cref="SubmitChat"/> is called from the network thread that is in the
/// middle of handling a chat packet, so it must never block, allocate unboundedly, or
/// throw. It formats a string and enqueues it; that is all.
///
/// Second, Discord rate-limits webhooks at roughly 5 requests per 2 seconds each. A
/// POST per chat line would trip that the moment Trade got busy, so lines are
/// coalesced: one post per channel per <see cref="Settings.BatchSeconds"/>, carrying
/// everything said in that window. Chat arrives in Discord a second or two late and
/// grouped, which reads better anyway.
/// </summary>
internal sealed class ChatRelay : IDisposable
{
    /// <summary>Discord's hard cap on a message is 2000 characters; leave room for the join.</summary>
    private const int DiscordContentLimit = 1900;

    /// <summary>Posts allowed per channel per tick, so a flood cannot become a burst of requests.</summary>
    private const int MaxPostsPerFlush = 3;

    private readonly Settings _settings;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _queues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Channels whose webhook Discord has rejected outright (deleted, or a bad token).
    /// Retrying those just burns a request every tick forever, so they are parked until
    /// the next /discordrelay-reload.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _broken = new(StringComparer.OrdinalIgnoreCase);

    private long _relayed, _dropped, _posts, _failures;
    private volatile bool _stopping;
    private volatile string _lastError = "";
    private DateTime _lastPostUtc;

    public long Relayed => Interlocked.Read(ref _relayed);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Posts => Interlocked.Read(ref _posts);
    public long Failures => Interlocked.Read(ref _failures);
    public string LastError => _lastError;
    public DateTime LastPostUtc => _lastPostUtc;

    public ChatRelay(Settings settings)
    {
        _settings = settings;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Aeshnidae-DiscordRelay/1.0");

        _pump = Task.Run(PumpAsync);
    }

    public int QueueDepth(string channelKey) =>
        _queues.TryGetValue(channelKey, out var q) ? q.Count : 0;

    public string? BrokenReason(string channelKey) =>
        _broken.TryGetValue(channelKey, out var reason) ? reason : null;

    /// <summary>
    /// Called from the chat patch, on ACE's network thread. Cheap and total: every
    /// rejection is a silent return, and nothing here can throw into the caller.
    /// </summary>
    public void SubmitChat(ChatType chatType, string? name, string? message)
    {
        if (!_settings.Enabled || _stopping)
            return;

        if (string.IsNullOrWhiteSpace(message) || _settings.IsIgnored(name))
            return;

        var key = Settings.Key(chatType);

        if (_settings.For(chatType) is null || _broken.ContainsKey(key))
            return;

        var line = _settings.LineFormat
            .Replace("{name}", Escape(name ?? "someone"))
            .Replace("{message}", Escape(Truncate(message!, Math.Max(1, _settings.MaxMessageLength))))
            .Replace("{channel}", key);

        Submit(key, line);

        if (_settings.LogRelayed)
            ModManager.Log($"[{Mod.Name}] {key}: {name}: {message}");
    }

    /// <summary>Queue an already-formatted line for a channel. Used by /discordrelay-test too.</summary>
    public void Submit(string channelKey, string line)
    {
        var queue = _queues.GetOrAdd(channelKey, _ => new ConcurrentQueue<string>());

        // Discord being down must not turn into an ever-growing queue on a live server.
        if (queue.Count >= Math.Max(1, _settings.MaxQueuedPerChannel))
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        queue.Enqueue(line);
        Interlocked.Increment(ref _relayed);
    }

    private async Task PumpAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.BatchSeconds, 0.5, 60));

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

        // One last drain on the way out, on its own budget: the token above is already
        // cancelled by now and would abort the post before it left the building.
        using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(final.Token);
    }

    private async Task FlushAsync(CancellationToken ct)
    {
        foreach (var (key, queue) in _queues)
        {
            if (queue.IsEmpty || _broken.ContainsKey(key))
                continue;

            if (!_settings.Channels.TryGetValue(key, out var channel) || channel is null
                || !channel.Enabled || string.IsNullOrWhiteSpace(channel.WebhookUrl))
                continue;

            foreach (var content in TakeBatches(queue))
            {
                if (ct.IsCancellationRequested)
                    return;

                if (!await PostAsync(key, channel, content, ct))
                    break;   // channel is unhappy; leave the rest for the next tick
            }
        }
    }

    /// <summary>
    /// Pulls up to <see cref="MaxPostsPerFlush"/> messages' worth of lines off a queue,
    /// packing them to just under Discord's length cap. Anything left stays queued.
    /// </summary>
    private static List<string> TakeBatches(ConcurrentQueue<string> queue)
    {
        var posts = new List<string>();
        var sb = new StringBuilder();

        while (posts.Count < MaxPostsPerFlush && queue.TryPeek(out var peeked))
        {
            var line = Truncate(peeked, DiscordContentLimit);

            if (sb.Length > 0 && sb.Length + 1 + line.Length > DiscordContentLimit)
            {
                posts.Add(sb.ToString());
                sb.Clear();

                if (posts.Count == MaxPostsPerFlush)
                    break;
            }

            queue.TryDequeue(out _);

            if (sb.Length > 0)
                sb.Append('\n');

            sb.Append(line);
        }

        if (sb.Length > 0 && posts.Count < MaxPostsPerFlush)
            posts.Add(sb.ToString());

        return posts;
    }

    private async Task<bool> PostAsync(string key, ChannelSettings channel, string content, CancellationToken ct)
    {
        var payload = new WebhookPayload
        {
            Content = content,
            Username = string.IsNullOrWhiteSpace(channel.Username) ? null : SanitizeUsername(channel.Username),
        };

        var json = JsonSerializer.Serialize(payload, PayloadOptions);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var body = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync(channel.WebhookUrl, body, ct);

                if (response.IsSuccessStatusCode)
                {
                    Interlocked.Increment(ref _posts);
                    _lastPostUtc = DateTime.UtcNow;
                    return true;
                }

                // Rate limited: Discord tells us how long to wait. One retry, then give up
                // on this tick - the next flush carries whatever is still queued.
                if ((int)response.StatusCode == 429 && attempt == 0)
                {
                    await Task.Delay(RetryAfter(response), ct);
                    continue;
                }

                Interlocked.Increment(ref _failures);
                _lastError = $"{key}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

                // 401/403/404 mean the webhook itself is wrong or gone. Retrying every
                // couple of seconds until someone notices helps nobody.
                if ((int)response.StatusCode is 401 or 403 or 404)
                {
                    _broken[key] = $"HTTP {(int)response.StatusCode} - webhook rejected; fix the URL, then /discordrelay-reload";

                    ModManager.Log($"[{Mod.Name}] {key} webhook rejected with HTTP {(int)response.StatusCode}; " +
                                   "relay for that channel is parked until reload.", ModManager.LogLevel.Error);
                }

                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failures);
                _lastError = $"{key}: {ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        return false;
    }

    /// <summary>How long Discord asked us to wait, clamped to something sane.</summary>
    private static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;

        var seconds = retryAfter?.Delta?.TotalSeconds
                      ?? (retryAfter?.Date is { } date ? (date - DateTimeOffset.UtcNow).TotalSeconds : 2);

        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 30));
    }

    /// <summary>
    /// Neutralises Discord markdown in player-supplied text, so a name full of asterisks
    /// cannot reformat the channel, and flattens anything that would break the one
    /// line-per-message layout. Pings are handled separately, by allowed_mentions.
    /// </summary>
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

    /// <summary>
    /// Cuts to <paramref name="max"/> characters including the ellipsis, without
    /// splitting a surrogate pair - AC chat is UTF-16 off the wire, and half of an
    /// emoji is not valid text for a JSON payload.
    /// </summary>
    private static string Truncate(string text, int max)
    {
        if (text.Length <= max)
            return text;

        var cut = Math.Max(0, max - 1);

        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
            cut--;

        return text[..cut] + "…";
    }

    /// <summary>Discord refuses webhook usernames containing "discord", and caps them at 80.</summary>
    private static string SanitizeUsername(string name) =>
        Truncate(name.Replace("discord", "disc0rd", StringComparison.OrdinalIgnoreCase), 80);

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _cts.Cancel();

            // Bounded: this runs on the shutdown / mod-disable path, and the pump's final
            // flush has its own 5s budget on top of the HttpClient timeout.
            _pump.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] relay did not stop cleanly: {ex.Message}", ModManager.LogLevel.Warn);
        }
        finally
        {
            _http.Dispose();
            _cts.Dispose();
        }
    }

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed class WebhookPayload
    {
        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        /// <summary>
        /// The important one. An empty parse list tells Discord to render @everyone,
        /// @here and role mentions as plain text, so nothing a player types in Trade can
        /// ping the whole server.
        /// </summary>
        [JsonPropertyName("allowed_mentions")]
        public AllowedMentions AllowedMentions { get; set; } = new();
    }

    private sealed class AllowedMentions
    {
        [JsonPropertyName("parse")]
        public string[] Parse { get; set; } = Array.Empty<string>();
    }
}
