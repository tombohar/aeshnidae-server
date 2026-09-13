namespace Aeshnidae.RemoteConsole;

public static class Commands
{
    [CommandHandler("remoteconsole", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Where the remote console inbox is, and whether it is being watched.",
        "/remoteconsole")]
    public static void HandleStatus(Session session, params string[] parameters)
    {
        var container = Mod.Container;

        var pending = 0;
        try { pending = Directory.GetFiles(Inbox.Directory, "*.cmd").Length; } catch { }

        Reply(session,
            $"{Mod.Name} v{container?.Meta.Version ?? "?"} - {container?.Status.ToString() ?? "not loaded"}\n" +
            $"inbox: {Inbox.Directory}\n" +
            $"poll every {Mod.Settings.PollSeconds:0.##}s, {pending} file(s) waiting\n" +
            "Drop <name>.cmd there, one console command per line; a <name>.done receipt appears when it has run.");
    }

    [CommandHandler("remoteconsole-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the Aeshnidae.RemoteConsole mod, re-reading Settings.json.",
        "/remoteconsole-reload")]
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
