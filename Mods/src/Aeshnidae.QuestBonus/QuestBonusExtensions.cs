namespace Aeshnidae.QuestBonus;

public static class QuestBonusExtensions
{
    /// <summary>
    /// Where a character's accumulated quest points are stored.
    ///
    /// This is a "fake" PropertyFloat: ACE's own PropertyFloat enum currently stops
    /// at 171, so 20002 cannot collide, and the shard DB persists unknown property
    /// ids just fine. 20002 is deliberately the same id Aquafir's ACE.Shared uses
    /// for FakeFloat.QuestBonus, so the data stays interchangeable with his mods.
    ///
    /// Changing this orphans every character's stored total (they resync on next
    /// login, but the old rows linger), so treat it as fixed.
    /// </summary>
    public const PropertyFloat QuestBonusProperty = (PropertyFloat)20002;

    /// <summary>Replaces ACE.Shared's QuestExtensions.HasSolves.</summary>
    public static bool HasSolves(this CharacterPropertiesQuestRegistry quest) => quest.NumTimesCompleted != 0;

    /// <summary>Points a quest is worth, by name. Falls back to Settings.DefaultPoints.</summary>
    public static double WeightOf(string questFormat)
    {
        var name = QuestManager.GetQuestName(questFormat);

        return Mod.Settings.QuestWeights.TryGetValue(name, out var weight)
            ? weight
            : Mod.Settings.DefaultPoints;
    }

    public static double GetQuestPoints(this Player player) =>
        player.GetProperty(QuestBonusProperty) ?? 0;

    public static void SetQuestPoints(this Player player, double points) =>
        player.SetProperty(QuestBonusProperty, Math.Max(0, points));

    public static void AddQuestPoints(this Player player, double delta) =>
        player.SetQuestPoints(player.GetQuestPoints() + delta);

    /// <summary>Sums the weights of every quest this character has actually solved.</summary>
    public static double CalculateQuestPoints(this Player player)
    {
        double total = 0;

        foreach (var quest in player.QuestManager.GetQuests())
        {
            if (quest.HasSolves())
                total += WeightOf(quest.QuestName);
        }

        return total;
    }

    /// <summary>
    /// Recomputes from scratch and stores the result. This is the authoritative
    /// path; the incremental Add/Subtract patches are just to keep it live.
    /// </summary>
    public static void ResyncQuestPoints(this Player player) =>
        player.SetQuestPoints(player.CalculateQuestPoints());

    /// <summary>
    /// The XP multiplier: 1 + points * BonusConversion, capped by MaxMultiplier.
    /// Always >= 1.
    /// </summary>
    public static double QuestBonusMultiplier(this Player player)
    {
        var multiplier = 1 + player.GetQuestPoints() * Mod.Settings.BonusConversion;

        if (Mod.Settings.MaxMultiplier > 0)
            multiplier = Math.Min(multiplier, Mod.Settings.MaxMultiplier);

        return Math.Max(1, multiplier);
    }

    /// <summary>"+4.20%" - the bonus on its own, which reads better than a raw multiplier.</summary>
    public static string QuestBonusText(this Player player) =>
        $"+{(player.QuestBonusMultiplier() - 1) * 100:0.##}%";
}
