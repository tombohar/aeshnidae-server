namespace Aeshnidae.AdminAudit;

/// <summary>
/// The command an admin is executing on this thread, right now.
///
/// ACE runs a command handler synchronously on the thread that received the packet, so
/// anything a handler does - conjuring an item, granting XP - happens between the
/// bracketing patches on <c>GameActionTalk.Handle</c>. Recording that frame lets an
/// outcome hook say *which* command caused it, and, more usefully, lets hooks on
/// methods that gameplay also uses fire only when a command is actually running.
///
/// <c>Player.TryCreateInInventoryWithNetworking</c> is the reason this exists: 29 call
/// sites, most of them ordinary gameplay. Gated on an active frame it means exactly
/// "an admin conjured this", with no gameplay noise at all.
/// </summary>
internal sealed class CommandFrame
{
    public string Actor = "";
    public string? Account;
    public AccessLevel Access;
    public string Command = "";
    public string Detail = "";
    public string Source = "ingame";
    public bool ReadOnly;
}

internal static class CommandContext
{
    [ThreadStatic]
    private static CommandFrame? _current;

    public static CommandFrame? Current => _current;

    public static void Set(CommandFrame frame) => _current = frame;

    public static void Clear() => _current = null;
}

/// <summary>
/// Front door for every audit record. Applies the access-level filter once, in one
/// place, then fans out to the sinks.
/// </summary>
internal sealed class Auditor : IDisposable
{
    private readonly Settings _settings;

    public AuditLog Log { get; }
    /// <summary>The default sink - what unrouted events go to. Null when Discord is off.</summary>
    public DiscordSink? Discord { get; }

    /// <summary>Every sink, default first. One per distinct webhook; routes sharing a webhook share a sink.</summary>
    public IReadOnlyList<DiscordSink> Sinks => _sinks;

    private readonly List<DiscordSink> _sinks = new();
    private readonly List<(string Prefix, DiscordSink Sink)> _routes = new();

    private long _recorded, _filtered;

    public long Recorded => Interlocked.Read(ref _recorded);
    public long Filtered => Interlocked.Read(ref _filtered);

    public Auditor(Settings settings, string modPath)
    {
        _settings = settings;

        Log = new AuditLog(settings.ResolveDirectory(modPath), settings.Log);

        if (settings.Discord.Enabled && !string.IsNullOrWhiteSpace(settings.Discord.WebhookUrl))
        {
            Discord = new DiscordSink(settings.Discord, settings.Discord.WebhookUrl);
            _sinks.Add(Discord);

            foreach (var (prefix, url) in settings.Discord.Routes ?? new())
            {
                if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(url))
                    continue;

                // A route pointed at the default webhook, or at one another route already
                // uses, reuses that sink - one pump per webhook keeps the rate limiting honest.
                var sink = _sinks.FirstOrDefault(x => string.Equals(x.WebhookUrl, url, StringComparison.Ordinal));
                if (sink is null)
                {
                    sink = new DiscordSink(settings.Discord, url, prefix);
                    _sinks.Add(sink);
                }

                _routes.Add((prefix, sink));
            }
        }
    }

    /// <summary>Longest matching prefix wins; nothing matching falls through to the default.</summary>
    private DiscordSink? SinkFor(AuditEvent record)
    {
        if (_routes.Count == 0 || string.IsNullOrEmpty(record.Detail))
            return Discord;

        DiscordSink? best = null;
        var bestLen = -1;

        foreach (var (prefix, sink) in _routes)
        {
            if (prefix.Length > bestLen && record.Detail.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                best = sink;
                bestLen = prefix.Length;
            }
        }

        return best ?? Discord;
    }

    /// <summary>
    /// Records an event. Total by contract: callers are Harmony patches on hot server
    /// paths, so this swallows everything rather than letting an audit failure surface
    /// as a gameplay bug.
    /// </summary>
    public void Record(AuditEvent record)
    {
        try
        {
            if (!_settings.Enabled)
                return;

            Interlocked.Increment(ref _recorded);

            Log.Write(record);
            SinkFor(record)?.Post(record);

            if (_settings.EchoToServerLog)
                ModManager.Log($"[AUDIT] {record.ToLine()}");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] failed to record an audit event: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>Should this actor be audited at all? Counts what it turns away, so /adminaudit can show it.</summary>
    public bool ShouldAudit(AccessLevel access, string? name)
    {
        if (_settings.ShouldAudit(access, name))
            return true;

        Interlocked.Increment(ref _filtered);
        return false;
    }

    /// <summary>Fills in the who-and-where fields that every record shares.</summary>
    public static AuditEvent For(Player? player, AuditKind kind, string action)
    {
        var record = new AuditEvent
        {
            Kind = kind.ToString(),
            Action = action,
            Actor = player?.Name ?? "CONSOLE",
            Account = player?.Account?.AccountName,
            Access = (player?.Session?.AccessLevel ?? AccessLevel.Player).ToString(),
            Source = player is null ? "console" : "ingame",
        };

        try
        {
            record.Location = player?.Location?.ToLOCString();
        }
        catch
        {
            // A player mid-teleport can have a location that will not render. Not worth
            // losing the record over.
        }

        // Attribute the event to the command that caused it, when there was one.
        if (CommandContext.Current is { } frame)
        {
            record.Via = frame.Command;
            record.ReadOnly = frame.ReadOnly;
        }

        return record;
    }

    public void Dispose()
    {
        foreach (var sink in _sinks) sink.Dispose();
        Log.Dispose();
    }
}
