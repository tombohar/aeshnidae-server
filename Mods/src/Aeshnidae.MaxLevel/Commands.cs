namespace Aeshnidae.MaxLevel;

public static class Commands
{
    [CommandHandler("maxlevel", AccessLevel.Player, CommandHandlerFlag.None, -1,
        "Show the level cap and what the next levels cost. '/maxlevel <n>' for one level.",
        "/maxlevel [level]")]
    public static void HandleMaxLevel(Session session, params string[] parameters)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Level cap: {Player.GetMaxLevel()} (retail {LevelTable.RetailMaxLevel})");

        var xp = DatManager.PortalDat?.XpTable?.CharacterLevelXPList;
        if (xp is null || xp.Count == 0)
        {
            Reply(session, sb.ToString());
            return;
        }

        if (parameters is { Length: > 0 } && int.TryParse(parameters[0], out var level))
        {
            if (level < 1 || level >= xp.Count)
            {
                Reply(session, $"Level must be between 1 and {xp.Count - 1}.");
                return;
            }

            var cost = level > 0 ? xp[level] - xp[level - 1] : 0;
            sb.AppendLine($"Level {level}: {xp[level]:N0} total XP, {cost:N0} for that level.");
        }
        else
        {
            sb.AppendLine($"Total XP at {xp.Count - 1}: {xp[^1]:N0}");

            var player = session?.Player;
            if (player?.Level is int cur && cur + 1 < xp.Count)
            {
                var next = xp[cur + 1] - xp[cur];
                sb.AppendLine($"You are {cur}. Next level costs {next:N0}.");
            }
        }

        Reply(session, sb.ToString());
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
