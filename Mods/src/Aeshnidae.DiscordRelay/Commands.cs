namespace Aeshnidae.DiscordRelay;

/// <summary>
/// Chat / console commands added by this mod.
///
/// ModContainer scans the assembly for [CommandHandler] methods and registers them
/// when the mod is enabled (Meta.json "RegisterCommands": true), then removes them
/// again on Disable. Handlers must be public static and match ACE's CommandHandler
/// delegate exactly: (Session session, params string[] parameters).
///
/// All three are Admin-only: the status line names the Discord channels this server
/// pipes chat into, which is not something to hand out at Player access.
/// </summary>
public static class Commands
{
    [CommandHandler("discordrelay", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Show the Discord chat relay's status.",
        "/discordrelay")]
    public static void HandleStatus(Session session, params string[] parameters)
    {
        var container = Mod.Container;
        var relay = Mod.Relay;

        var text = new StringBuilder()
            .AppendLine($"{Mod.Name} v{container?.Meta.Version ?? "?"} - {container?.Status.ToString() ?? "not loaded"}")
            .AppendLine($"Enabled: {Mod.Settings.Enabled}   batch: {Mod.Settings.BatchSeconds:0.#}s   relay: {(relay is null ? "not running" : "running")}");

        foreach (var (key, channel) in Mod.Settings.Channels.OrderBy(c => c.Key))
        {
            var state = !channel.Enabled ? "off"
                : string.IsNullOrWhiteSpace(channel.WebhookUrl) ? "no webhook set"
                : relay?.BrokenReason(key) ?? $"-> {MaskWebhook(channel.WebhookUrl)}";

            var queued = relay?.QueueDepth(key) ?? 0;

            text.AppendLine($"  {key,-10} {state}{(queued > 0 ? $"   ({queued} queued)" : "")}");
        }

        if (relay is not null)
        {
            text.AppendLine($"Queued {relay.Relayed}, dropped {relay.Dropped}, posts {relay.Posts}, failures {relay.Failures}");
            text.AppendLine($"Last post: {(relay.LastPostUtc == default ? "never" : $"{(DateTime.UtcNow - relay.LastPostUtc).TotalSeconds:0}s ago")}");

            if (!string.IsNullOrEmpty(relay.LastError))
                text.AppendLine($"Last error: {relay.LastError}");
        }

        Reply(session, text.ToString());
    }

    [CommandHandler("discordrelay-test", AccessLevel.Admin, CommandHandlerFlag.None, 1,
        "Post a test line to one relayed channel's Discord webhook.",
        "/discordrelay-test <General|Trade|...>")]
    public static void HandleTest(Session session, params string[] parameters)
    {
        var relay = Mod.Relay;

        if (relay is null)
        {
            Reply(session, $"{Mod.Name} is not running.");
            return;
        }

        var key = Mod.Settings.Channels.Keys.FirstOrDefault(k => string.Equals(k, parameters[0], StringComparison.OrdinalIgnoreCase));

        if (key is null)
        {
            Reply(session, $"No channel called \"{parameters[0]}\" in Settings.json. Known: {string.Join(", ", Mod.Settings.Channels.Keys)}");
            return;
        }

        var who = session?.Player?.Name ?? "the console";

        relay.Submit(key, $"_Relay test for **{key}**, sent from {who}._");
        Reply(session, $"Test line queued for {key}; it should appear within {Mod.Settings.BatchSeconds:0.#}s. Check /discordrelay if it does not.");
    }

    [CommandHandler("discordrelay-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the Discord relay, re-reading Settings.json.",
        "/discordrelay-reload")]
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
    /// A webhook URL is a credential, so the status line proves one is set without
    /// printing it: host, the webhook id, and the token replaced.
    /// </summary>
    private static string MaskWebhook(string url)
    {
        try
        {
            var uri = new Uri(url);
            var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToArray();
            var id = segments.Length >= 2 ? segments[^2] : "?";

            return $"{uri.Host}/.../{id}/****";
        }
        catch
        {
            return "set (unparseable URL)";
        }
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
