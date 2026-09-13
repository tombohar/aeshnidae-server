namespace Aeshnidae.AdminAudit;

/// <summary>
/// The system of record: one JSON object per line, one file per UTC day.
///
/// Append-only and never rewritten, so a record that has been flushed cannot be
/// disturbed by anything this mod does later. Writes happen on a background task
/// because <see cref="Auditor.Record"/> is called from the world and network threads
/// and must never wait on a disk.
///
/// The flush interval is the honest limit on durability: a hard crash loses whatever
/// is still queued. It defaults to one second rather than the chat relay's two,
/// because losing an audit record matters more than losing a line of Trade chat.
/// </summary>
internal sealed class AuditLog : IDisposable
{
    private readonly string _directory;
    private readonly LogSettings _settings;
    private readonly ConcurrentQueue<AuditEvent> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    private long _written, _dropped;
    private volatile bool _stopping;
    private volatile string _lastError = "";
    private string _currentFile = "";

    public long Written => Interlocked.Read(ref _written);
    public long Dropped => Interlocked.Read(ref _dropped);
    public string LastError => _lastError;
    public string Directory => _directory;
    public string CurrentFile => _currentFile;
    public int Pending => _queue.Count;

    /// <summary>
    /// UTF-8 with no byte-order mark. <c>Encoding.UTF8</c> emits a BOM when it creates
    /// the file, which lands in front of the first record - System.Text.Json tolerates
    /// that, but strict parsers (Python's json, jq on some builds) reject the line. A
    /// JSONL trail whose whole point is being readable by ordinary tools cannot start
    /// with three bytes that break them.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public AuditLog(string directory, LogSettings settings)
    {
        _directory = directory;
        _settings = settings;

        System.IO.Directory.CreateDirectory(_directory);
        Prune();

        _pump = Task.Run(PumpAsync);
    }

    public void Write(AuditEvent record)
    {
        if (_stopping)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(record);
    }

    public string FileFor(DateTime utcDay) =>
        Path.Combine(_directory, $"audit-{utcDay:yyyyMMdd}.jsonl");

    private async Task PumpAsync()
    {
        var interval = TimeSpan.FromSeconds(Math.Clamp(_settings.FlushSeconds, 0.25, 30));
        var lastPruneDay = DateTime.UtcNow.Date;

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

            Flush();

            if (DateTime.UtcNow.Date != lastPruneDay)
            {
                lastPruneDay = DateTime.UtcNow.Date;
                Prune();
            }
        }

        Flush();
    }

    /// <summary>
    /// Drains the queue into today's file in one open/append/close. Grouped by day so a
    /// batch that straddles midnight lands in the right files rather than all in one.
    /// </summary>
    private void Flush()
    {
        if (_queue.IsEmpty)
            return;

        var batch = new List<AuditEvent>();

        while (_queue.TryDequeue(out var record))
            batch.Add(record);

        foreach (var day in batch.GroupBy(r => DayOf(r.Timestamp)))
        {
            var path = FileFor(day.Key);

            try
            {
                var text = new StringBuilder();

                foreach (var record in day)
                    text.AppendLine(JsonSerializer.Serialize(record, LineOptions));

                File.AppendAllText(path, text.ToString(), Utf8NoBom);

                _currentFile = path;
                Interlocked.Add(ref _written, day.Count());
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _dropped, day.Count());
                _lastError = $"{ex.GetType().Name}: {ex.Message}";

                // Logged, not thrown: an audit sink that takes the server down when the
                // disk fills is a worse outcome than a gap in the trail. The gap is
                // counted and visible in /adminaudit.
                ModManager.Log($"[{Mod.Name}] could not append to {path}: {ex.Message}", ModManager.LogLevel.Error);
            }
        }
    }

    private static DateTime DayOf(string timestamp) =>
        DateTime.TryParse(timestamp, null, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToUniversalTime().Date
            : DateTime.UtcNow.Date;

    /// <summary>
    /// Deletes audit files past the retention window. Retention defaults to 0 - keep
    /// everything - because for an audit trail, silently discarding history is the
    /// surprising behaviour, not the safe one.
    /// </summary>
    private void Prune()
    {
        if (_settings.RetentionDays <= 0)
            return;

        try
        {
            var cutoff = DateTime.UtcNow.Date.AddDays(-_settings.RetentionDays);

            foreach (var path in System.IO.Directory.GetFiles(_directory, "audit-*.jsonl"))
            {
                var stamp = Path.GetFileNameWithoutExtension(path).Replace("audit-", "");

                if (DateTime.TryParseExact(stamp, "yyyyMMdd", null, DateTimeStyles.None, out var day) && day < cutoff)
                {
                    File.Delete(path);
                    ModManager.Log($"[{Mod.Name}] pruned audit file past retention: {Path.GetFileName(path)}");
                }
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] prune failed: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    /// <summary>Most recent lines from the audit files, newest last. Used by /adminaudit tail and search.</summary>
    public List<string> Tail(int count, string? filter = null)
    {
        var lines = new List<string>();

        try
        {
            var files = System.IO.Directory.GetFiles(_directory, "audit-*.jsonl")
                .OrderByDescending(f => f)
                .Take(7);   // a week of files is plenty to satisfy any reasonable tail

            foreach (var file in files)
            {
                // Read a copy - the pump may be appending to this very file.
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                string? line;
                var fromFile = new List<string>();

                while ((line = reader.ReadLine()) is not null)
                {
                    if (filter is null || line.Contains(filter, StringComparison.OrdinalIgnoreCase))
                        fromFile.Add(line);
                }

                lines.InsertRange(0, fromFile);

                if (lines.Count >= count)
                    break;
            }
        }
        catch (Exception ex)
        {
            _lastError = $"{ex.GetType().Name}: {ex.Message}";
        }

        return lines.Count <= count ? lines : lines.GetRange(lines.Count - count, count);
    }

    public void Dispose()
    {
        _stopping = true;

        try
        {
            _cts.Cancel();
            _pump.Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] audit log did not stop cleanly: {ex.Message}", ModManager.LogLevel.Warn);
        }
        finally
        {
            Flush();   // belt and braces: anything queued between Cancel and here
            _cts.Dispose();
        }
    }
}
