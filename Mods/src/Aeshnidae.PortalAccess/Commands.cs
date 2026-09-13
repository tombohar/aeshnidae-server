namespace Aeshnidae.PortalAccess;

public static class Commands
{
    [CommandHandler("portalaccess", AccessLevel.Advocate, CommandHandlerFlag.None, 0,
        "Show which portal level requirements are currently suppressed.",
        "/portalaccess")]
    public static void HandleStatus(Session session, params string[] parameters)
    {
        var sb = new StringBuilder()
            .AppendLine($"{Mod.Name} - {Mod.Container?.Status.ToString() ?? "not loaded"}")
            .AppendLine($"  portal minimum level : {(Mod.Settings.RemoveMinLevel ? "removed" : "enforced")}")
            .AppendLine($"  portal maximum level : {(Mod.Settings.RemoveMaxLevel ? "removed" : "enforced")}")
            .AppendLine("  Housing level requirements are unaffected - only Portal objects are patched.");

        Reply(session, sb.ToString());
    }

    [CommandHandler("portalaccess-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart Aeshnidae.PortalAccess, re-reading Settings.json.",
        "/portalaccess-reload")]
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
