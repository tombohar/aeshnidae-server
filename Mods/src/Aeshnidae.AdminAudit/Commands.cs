namespace Aeshnidae.AdminAudit;

/// <summary>
/// Admin-only, all of them: the audit trail names who did what, and its own status
/// tells you whether anyone would notice if you did something.
/// </summary>
public static class Commands
{
    [CommandHandler("adminaudit", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Audit trail status, tail and search.",
        "/adminaudit [ hooks | tail [n] | find <text> [n] | rebind ]")]
    public static void HandleAudit(Session session, params string[] parameters)
    {
        try
        {
            var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

            switch (verb)
            {
                case "hooks": Hooks(session); return;
                case "rebind": Rebind(session); return;
                case "tail": Tail(session, Count(parameters, 1, 20), null); return;
                case "find" when parameters.Length > 1:
                    Tail(session, Count(parameters, 2, 20), parameters[1]);
                    return;
                default: Status(session); return;
            }
        }
        catch (Exception ex)
        {
            Reply(session, $"/adminaudit failed: {ex.Message}");
            ModManager.Log($"[{Mod.Name}] /adminaudit failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    private static void Status(Session? session)
    {
        var container = Mod.Container;
        var audit = Mod.Auditor;

        var text = new StringBuilder()
            .AppendLine($"{Mod.Name} v{container?.Meta.Version ?? "?"} - {container?.Status.ToString() ?? "not loaded"}");

        if (audit is null)
        {
            text.AppendLine("Not recording. Check the server log for why.");
            Reply(session, text.ToString());
            return;
        }

        var uptime = DateTime.UtcNow - Mod.StartedUtc;

        text.AppendLine($"Recording since {Mod.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} ({uptime.TotalHours:0.#}h), threshold {Mod.Settings.Threshold}")
            .AppendLine($"Directory: {audit.Log.Directory}")
            .AppendLine($"Recorded {audit.Recorded:N0}, written {audit.Log.Written:N0}, pending {audit.Log.Pending}, dropped {audit.Log.Dropped:N0}")
            .AppendLine($"Below threshold, not recorded: {audit.Filtered:N0}");

        if (!string.IsNullOrEmpty(audit.Log.LastError))
            text.AppendLine($"Last file error: {audit.Log.LastError}");

        if (audit.Sinks.Count > 0)
        {
            foreach (var sink in audit.Sinks)
            {
                text.AppendLine($"Discord [{sink.Label}]: {(sink.Parked ? "PARKED" : "on")}, posted {sink.Posted:N0}, pending {sink.Pending}, dropped {sink.Dropped:N0}");

                if (!string.IsNullOrEmpty(sink.LastError))
                    text.AppendLine($"  last error: {sink.LastError}");
            }
        }
        else
        {
            text.AppendLine("Discord: off");
        }

        var bound = SiblingPatches.Status.Count(h => h.Bound);
        text.AppendLine($"Sibling hooks: {bound}/{SiblingPatches.Status.Count} bound  (/adminaudit hooks)");

        Reply(session, text.ToString());
    }

    private static void Hooks(Session? session)
    {
        var text = new StringBuilder().AppendLine("Sibling-mod hooks:");

        foreach (var hook in SiblingPatches.Status)
            text.AppendLine($"  {(hook.Bound ? "[ok]  " : "[--]  ")}{hook.Target}  - {hook.Note}");

        if (SiblingPatches.Status.Count == 0)
            text.AppendLine("  none attempted (AuditSiblingMods is off)");

        text.AppendLine("Unbound hooks are normal if that mod is disabled. After /mod find, run /adminaudit rebind.");

        Reply(session, text.ToString());
    }

    private static void Rebind(Session? session)
    {
        var bound = Mod.Rebind();
        Reply(session, $"Rebound sibling hooks: {bound}/{SiblingPatches.Status.Count} bound.");
    }

    private static void Tail(Session? session, int count, string? filter)
    {
        var audit = Mod.Auditor;

        if (audit is null)
        {
            Reply(session, "Not recording.");
            return;
        }

        var lines = audit.Log.Tail(count, filter);

        if (lines.Count == 0)
        {
            Reply(session, filter is null ? "Nothing recorded yet." : $"No audit records matching \"{filter}\".");
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"Last {lines.Count} audit record(s){(filter is null ? "" : $" matching \"{filter}\"")}:");

        foreach (var line in lines)
            text.AppendLine("  " + Summarize(line));

        Reply(session, text.ToString());
    }

    /// <summary>Turns a stored JSONL line back into the one-line form, falling back to the raw line.</summary>
    private static string Summarize(string jsonLine)
    {
        try
        {
            return JsonSerializer.Deserialize<AuditEvent>(jsonLine)?.ToLine() ?? jsonLine;
        }
        catch
        {
            return jsonLine;
        }
    }

    private static int Count(string[] parameters, int index, int fallback) =>
        parameters.Length > index && int.TryParse(parameters[index], out var n) && n is > 0 and <= 200 ? n : fallback;

    [CommandHandler("adminaudit-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the audit mod, re-reading Settings.json.",
        "/adminaudit-reload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;

        if (container is null)
        {
            Reply(session, $"{Mod.Name} is not loaded.");
            return;
        }

        container.Restart();
        Reply(session, $"{Mod.Name} restarted.");
    }

    /// <summary>
    /// The client renders an embedded newline as a music note, so a multi-line
    /// message has to go out as one SendMessage per line.
    /// </summary>
    private static void Reply(Session? session, string message)
    {
        if (session?.Player is null)
        {
            ModManager.Log(message);
            return;
        }

        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                session.Player.SendMessage(text);
        }
    }
}
