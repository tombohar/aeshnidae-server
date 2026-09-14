namespace Aeshnidae.QuestBonus;

/// <summary>
/// The quest bonus's page in the Aeshnidae Codex. Found by Aeshnidae.Codex through
/// reflection (a static class called CodexPage with Title and Pages(Player)), so
/// nothing here references that mod.
/// </summary>
public static class CodexPage
{
    public static string Title => "Quest Bonus";

    public static string[] Pages(Player player)
    {
        if (player is null)
            return Array.Empty<string>();

        var points = player.GetQuestPoints();
        var multiplier = player.QuestBonusMultiplier();
        var settings = Mod.Settings;

        var sb = new StringBuilder();
        sb.AppendLine("QUEST BONUS");
        sb.AppendLine();
        sb.AppendLine($"Quests solved: {points:0.##}");
        sb.AppendLine($"Bonus to every experience award: {player.QuestBonusText()}");
        sb.AppendLine();

        if (settings.MaxMultiplier > 0)
        {
            var capPoints = (settings.MaxMultiplier - 1) / Math.Max(1e-9, settings.BonusConversion);

            if (multiplier >= settings.MaxMultiplier)
                sb.AppendLine($"You are at the ceiling of x{settings.MaxMultiplier:0.##}.");
            else
                sb.AppendLine($"Ceiling x{settings.MaxMultiplier:0.##}, at {capPoints:N0} quests. {Math.Max(0, capPoints - points):N0} to go.");

            sb.AppendLine();
        }

        sb.AppendLine($"Every quest solved for the first time adds {settings.DefaultPoints * settings.BonusConversion * 100:0.##}%, for good. Repeating one adds nothing.");
        sb.AppendLine("Timers, kill-task counters and stipend flags are not quests and do not count.");
        sb.AppendLine();
        sb.AppendLine("Radiance mirrors experience, so the bonus raises it too.");

        return new[] { sb.ToString().TrimEnd() };
    }
}
