namespace Aeshnidae.ClearWeenie;

public static class Commands
{
    [CommandHandler("clearweenie", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Reload one or more weenies from the database and refresh their live instances - without the global /clearcache spike.",
        "/clearweenie <wcid> [<wcid> ...]\n" +
        "/clearweenie 22642 3930        after a change to those NPCs' emotes\n" +
        "The wcids to pass are the ones listed in the change file's header.")]
    public static void HandleClearWeenie(Session session, params string[] parameters)
    {
        var wcids = new List<uint>();
        var bad = new List<string>();

        foreach (var raw in parameters)
        {
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (uint.TryParse(token, out var wcid) && wcid > 0)
                    wcids.Add(wcid);
                else
                    bad.Add(token);
            }
        }

        if (bad.Count > 0)
            Reply(session, $"Not a wcid: {string.Join(", ", bad)}. Numbers only - the ones in the change file's Wcids line.");

        if (wcids.Count == 0)
        {
            Reply(session, "Usage: /clearweenie <wcid> [<wcid> ...]");
            return;
        }

        foreach (var wcid in wcids.Distinct())
        {
            Refresh.Result result;

            try
            {
                result = Refresh.One(wcid);
            }
            catch (Exception ex)
            {
                Reply(session, $"{wcid}: failed - {ex.Message}");
                ModManager.Log($"[{Mod.Name}] /clearweenie {wcid} failed: {ex}", ModManager.LogLevel.Error);
                continue;
            }

            if (result.Error is not null)
            {
                Reply(session, $"{wcid}: {result.Error}");
                continue;
            }

            var line = $"{wcid} {result.Name}: reloaded" +
                       (result.WasCached ? "" : " (was not cached)") +
                       $", {result.LiveRefreshed} live object(s) refreshed across {result.LandblocksWalked} loaded landblock(s).";

            Reply(session, line);

            // From the console, Reply already went to the log - a second copy is noise.
            if (Mod.Settings.LogRefreshes && session?.Player is not null)
                ModManager.Log($"[{Mod.Name}] {session.Player.Name}: {line}");
        }
    }

    [CommandHandler("clearweenie-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the Aeshnidae.ClearWeenie mod, re-reading Settings.json.",
        "/clearweenie-reload")]
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
