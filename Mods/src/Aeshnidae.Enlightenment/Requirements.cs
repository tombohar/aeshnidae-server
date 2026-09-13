namespace Aeshnidae.Enlightenment;

/// <summary>
/// The eligibility gate, and the only place that decides it.
///
/// There are three places that could answer "may this player enlighten": the
/// Font's emote chain in the world database, ACE's Enlightenment.VerifyRequirements,
/// and this. Two of the three are silenced - the emote chain is intercepted before
/// its first check runs, and VerifyRequirements is prefixed to defer here - so
/// there is exactly one answer and Settings.json is what changes it.
///
/// Checks are collected rather than short-circuited. Retail told you about one
/// missing requirement per visit, which for a gate this expensive is a poor trade
/// for a few microseconds.
/// </summary>
public static class Requirements
{
    /// <summary>Level this player needs right now, given how many enlightenments they hold.</summary>
    public static int LevelFor(Player player) =>
        Mod.Settings.BaseLevelRequirement +
        Mod.Settings.LevelRequirementPerEnlightenment * Math.Max(0, player.Enlightenment);

    /// <summary>
    /// One row per aura: what the Font asks for, per its own emote tree in the world
    /// database (the InqIntStat rows on wcid 53412). The four ratings are checked at
    /// 5 there even though they go to 10; everything else is checked at its cap.
    ///
    /// Per aura rather than a sum, on purpose. ACE's VerifyLumAugs requires the sum to
    /// be exactly 65 - but fully maxed auras sum to 80, so stock ACE refuses a
    /// character who has bought everything, and a sum cannot tell a player which one
    /// they are missing anyway.
    /// </summary>
    public static readonly (string Name, PropertyInt Property, int Needed, int Max)[] Auras =
    {
        ("All Skills",       PropertyInt.LumAugAllSkills,            10, 10),
        ("Surge Chance",     PropertyInt.LumAugSurgeChanceRating,     5,  5),
        ("Damage",           PropertyInt.LumAugDamageRating,          5, 10),
        ("Damage Reduction", PropertyInt.LumAugDamageReductionRating, 5, 10),
        ("Crit Damage",      PropertyInt.LumAugCritDamageRating,      5, 10),
        ("Crit Reduction",   PropertyInt.LumAugCritReductionRating,   5, 10),
        ("Item Mana Usage",  PropertyInt.LumAugItemManaUsage,         5,  5),
        ("Item Mana Gain",   PropertyInt.LumAugItemManaGain,          5,  5),
        ("Healing",          PropertyInt.LumAugHealingRating,         5,  5),
        ("Skilled Craft",    PropertyInt.LumAugSkilledCraft,          5,  5),
        ("Skilled Spec",     PropertyInt.LumAugSkilledSpec,           5,  5),
    };

    /// <summary>Names of the auras below their threshold. Empty means the requirement is met.</summary>
    public static List<string> MissingAuras(Player player) =>
        Auras.Where(a => (player.GetProperty(a.Property) ?? 0) < a.Needed)
             .Select(a => a.Name)
             .ToList();

    public static bool IsSocietyMaster(Player player) =>
        player.SocietyRankCelhan == 1001 ||
        player.SocietyRankEldweb == 1001 ||
        player.SocietyRankRadblo == 1001;

    /// <summary>
    /// True when every requirement is met. <paramref name="failures"/> holds one
    /// player-facing line per unmet requirement, and is empty on success.
    /// </summary>
    public static bool Check(Player player, out List<string> failures)
    {
        failures = new List<string>();

        if (player is null)
            return false;

        var settings = Mod.Settings;

        if (settings.MaxEnlightenments > 0 && player.Enlightenment >= settings.MaxEnlightenments)
        {
            failures.Add($"You have already reached the maximum enlightenment level of {settings.MaxEnlightenments}.");

            // Nothing else is worth reporting once this is true - the other lines
            // would read as a to-do list for something that cannot happen.
            return false;
        }

        var requiredLevel = LevelFor(player);

        if ((player.Level ?? 0) < requiredLevel)
        {
            failures.Add(player.Enlightenment > 0
                ? $"You must be level {requiredLevel} for your {Ordinal(player.Enlightenment + 1)} enlightenment. You are level {player.Level ?? 0}."
                : $"You must be level {requiredLevel} for enlightenment. You are level {player.Level ?? 0}.");
        }

        if (settings.RequireAllLuminanceAuras)
        {
            var missing = MissingAuras(player);

            if (missing.Count > 0)
                failures.Add($"You must have all luminance auras for enlightenment. Missing: {string.Join(", ", missing)}.");
        }

        if (settings.RequireSocietyMaster && !IsSocietyMaster(player))
            failures.Add("You must be a Master of one of the Societies of Dereth for enlightenment.");

        if (settings.RequiredFreeInventorySlots > 0)
        {
            var free = player.GetFreeInventorySlots();

            if (free < settings.RequiredFreeInventorySlots)
                failures.Add($"You must have at least {settings.RequiredFreeInventorySlots} free inventory slots in your main pack for enlightenment. You have {free}.");
        }

        if (Guard.IsCoolingDown(player, out var remaining))
            failures.Add($"You are still basking in your last enlightenment. Try again in {remaining:N0} seconds.");

        return failures.Count == 0;
    }

    /// <summary>
    /// What a player sees from /enlighten: the same checks, phrased as a progress
    /// report rather than a refusal. Read-only by construction - this class has no
    /// method that changes anything.
    /// </summary>
    public static string Describe(Player player)
    {
        var settings = Mod.Settings;
        var sb = new StringBuilder();

        sb.AppendLine($"Enlightenment: {player.Enlightenment}" +
                      (settings.MaxEnlightenments > 0 ? $" of {settings.MaxEnlightenments}" : " (uncapped)"));

        if (settings.MaxEnlightenments > 0 && player.Enlightenment >= settings.MaxEnlightenments)
        {
            sb.AppendLine("  You have reached the maximum.");
            return sb.ToString().TrimEnd();
        }

        var requiredLevel = LevelFor(player);
        var level = player.Level ?? 0;

        sb.AppendLine($"  level              : {level,6:N0} / {requiredLevel,-6:N0} {Tick(level >= requiredLevel)}");

        if (settings.RequireAllLuminanceAuras)
        {
            var missing = MissingAuras(player);
            sb.AppendLine($"  luminance auras    : {Auras.Length - missing.Count,6:N0} / {Auras.Length,-6:N0} {Tick(missing.Count == 0)}" +
                          (missing.Count > 0 ? $"  (missing {string.Join(", ", missing)})" : ""));
        }

        if (settings.RequireSocietyMaster)
            sb.AppendLine($"  society master     : {Tick(IsSocietyMaster(player))}");

        if (settings.RequiredFreeInventorySlots > 0)
        {
            var free = player.GetFreeInventorySlots();
            sb.AppendLine($"  free pack slots    : {free,6:N0} / {settings.RequiredFreeInventorySlots,-6:N0} {Tick(free >= settings.RequiredFreeInventorySlots)}");
        }

        sb.AppendLine();
        sb.AppendLine("On enlightening you would keep " + Join(Keeps()) + ",");
        sb.AppendLine("and lose " + Join(Losses()) + ".");
        sb.AppendLine($"You would gain +{player.Enlightenment + 1} to all trained skills and +{(player.Enlightenment + 1) * 2} vitality in total.");
        sb.AppendLine();
        sb.AppendLine("Enlightenment is granted only by the Font of Enlightenment and Rebirth. There is no command for it.");

        return sb.ToString().TrimEnd();
    }

    private static List<string> Keeps()
    {
        var settings = Mod.Settings;
        var keeps = new List<string>();

        if (settings.KeepSociety) keeps.Add("your society rank");
        if (settings.KeepLuminanceAuras) keeps.Add("your luminance auras");
        if (settings.KeepLuminanceAccess) keeps.Add("the ability to earn luminance");
        if (settings.KeepUnassignedExperience) keeps.Add("your unassigned experience");
        if (settings.KeepAetheria) keeps.Add("your aetheria");

        return keeps;
    }

    private static List<string> Losses()
    {
        var settings = Mod.Settings;
        var losses = new List<string>();

        if (settings.ResetLevel) losses.Add("your level and spent experience");
        if (settings.ResetSkills) losses.Add("your skill ranks");
        if (settings.ResetAttributes) losses.Add("your attribute ranks");
        if (!settings.KeepAetheria) losses.Add("your aetheria");
        if (settings.DequipAllItems) losses.Add("the use of gear above your new level");

        return losses;
    }

    private static string Join(List<string> parts) => parts.Count switch
    {
        0 => "nothing",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    private static string Tick(bool ok) => ok ? "yes" : "no";

    internal static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        21 => "21st",
        22 => "22nd",
        23 => "23rd",
        _ => $"{n}th",
    };
}
