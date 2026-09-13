namespace Aeshnidae.QuestBonus;

public static class Commands
{
    [CommandHandler("qb", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Show your quest bonus. '/qb list' for a per-quest breakdown, '/qb sync' to recalculate.",
        "/qb [list|sync]")]
    public static void HandleQuestBonus(Session session, params string[] parameters)
    {
        var player = session?.Player;
        if (player is null)
            return;

        var verb = parameters is { Length: > 0 } ? parameters[0].ToLowerInvariant() : "";

        switch (verb)
        {
            case "list":
                ShowBreakdown(player);
                return;

            case "sync":
                player.ResyncQuestPoints();
                player.SendMessage($"Recalculated: {Summary(player)}");
                return;

            default:
                player.SendMessage(Summary(player));
                return;
        }
    }

    [CommandHandler("qbreload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Re-read Aeshnidae.QuestBonus Settings.json and resync every online character.",
        "/qbreload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;
        if (container is null)
        {
            Reply(session, $"{Mod.Name} is not loaded.");
            return;
        }

        // Restart runs Dispose then Initialize, which reloads settings and resyncs.
        container.Restart();
        Reply(session, $"{Mod.Name} reloaded: {Mod.Settings.BonusConversion * 100:0.###}% XP per quest point.");
    }

    private static string Summary(Player player)
    {
        var solved = player.QuestManager.GetQuests().Count(q => q.HasSolves());
        var points = player.GetQuestPoints();

        var text = $"{solved} quests solved, {points:0.##} QP, {player.QuestBonusText()} XP.";

        var uncapped = 1 + points * Mod.Settings.BonusConversion;
        if (Mod.Settings.MaxMultiplier > 0 && uncapped > Mod.Settings.MaxMultiplier)
            text += $" (capped at {Mod.Settings.MaxMultiplier:0.##}x)";

        return text;
    }

    private static void ShowBreakdown(Player player)
    {
        var quests = player.QuestManager.GetQuests()
            .OrderByDescending(q => q.HasSolves())
            .ThenBy(q => q.QuestName)
            .ToList();

        var sb = new StringBuilder($"{Summary(player)}\nQuest / completions / points\n");

        foreach (var quest in quests)
        {
            var weight = quest.HasSolves() ? QuestBonusExtensions.WeightOf(quest.QuestName) : 0;
            sb.AppendLine($"{quest.QuestName,-40} {quest.NumTimesCompleted,4}  {weight:0.##}");
        }

        player.SendMessage(sb.ToString());
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
