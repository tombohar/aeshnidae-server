namespace Aeshnidae.MaxLevel;

/// <summary>
/// Extends the client XP table in memory so the server accepts levels past retail.
///
/// Player.GetMaxLevel() is literally
///     DatManager.PortalDat.XpTable.CharacterLevelXPList.Count - 1
/// and CheckForLevelup indexes CharacterLevelSkillCreditList by level, so both
/// lists have to grow together or levelling past 275 throws IndexOutOfRange.
/// Both are `{ get; }` List properties - the objects are mutable even though the
/// properties are read-only, so no reflection is needed.
/// </summary>
public static class LevelTable
{
    /// <summary>Retail cap, captured the first time we touch the table.</summary>
    public static int RetailMaxLevel { get; private set; }

    public static int CurrentMaxLevel { get; private set; }

    public static bool Extended => CurrentMaxLevel > RetailMaxLevel && RetailMaxLevel > 0;

    /// <summary>Credits added to DeveloperFixCommands.AdditionalCredits, so we can remove exactly those.</summary>
    private static readonly List<int> _addedCreditLevels = new();

    /// <summary>
    /// The retail curve's growth law.
    ///
    /// Successive level costs satisfy  delta[L] / delta[L-1] == (L + 6) / (L + 2),
    /// which holds across the whole retail table and is essentially exact at the
    /// top of it - at L=275 it predicts 1.01444043 against 1.01444015 measured,
    /// and the small drift at low levels is just integer rounding in the dat.
    /// Continuing that recurrence is what "keep retail scaling" means here.
    /// </summary>
    private static double GrowthRatio(int level) => (level + 6.0) / (level + 2.0);

    public static bool TryExtend(XpTable table, Settings settings, out string message)
    {
        message = "";

        if (table is null)
        {
            message = "XpTable not loaded yet";
            return false;
        }

        var xp = table.CharacterLevelXPList;
        var credits = table.CharacterLevelSkillCreditList;

        if (xp.Count == 0 || credits.Count == 0)
        {
            message = "XpTable is empty";
            return false;
        }

        if (xp.Count != credits.Count)
        {
            message = $"XpTable is inconsistent: {xp.Count} xp entries vs {credits.Count} credit entries";
            return false;
        }

        // First contact: whatever the dat shipped is "retail".
        if (RetailMaxLevel == 0)
            RetailMaxLevel = xp.Count - 1;

        var target = settings.MaxLevel;

        if (target <= RetailMaxLevel)
        {
            message = $"MaxLevel {target} is not above retail {RetailMaxLevel}; leaving the table alone";
            CurrentMaxLevel = RetailMaxLevel;
            return false;
        }

        // Idempotent: a second call (dat reload, /mod restart) must not stack.
        if (xp.Count - 1 == target)
        {
            CurrentMaxLevel = target;
            message = $"already extended to {target}";
            return true;
        }

        if (xp.Count - 1 > RetailMaxLevel)
            Revert(table);

        // Seed from the last retail entries.
        var lastTotal = xp[RetailMaxLevel];
        var lastDelta = xp[RetailMaxLevel] - xp[RetailMaxLevel - 1];

        for (var level = RetailMaxLevel + 1; level <= target; level++)
        {
            // double is exact for integers up to 2^53; deltas top out around 3.6e10.
            lastDelta = (ulong)Math.Round(lastDelta * GrowthRatio(level));
            lastTotal += lastDelta;

            xp.Add(lastTotal);

            var milestone = settings.SkillCreditInterval > 0
                            && (level - RetailMaxLevel) % settings.SkillCreditInterval == 0;

            credits.Add(milestone ? settings.SkillCreditsPerInterval : 0u);
        }

        CurrentMaxLevel = target;
        ExtendCreditAudit(settings);

        message = $"level cap {RetailMaxLevel} -> {target}, "
                + $"{xp[target]:N0} total XP at {target}, "
                + $"+{CreditsAdded(settings)} skill credits";
        return true;
    }

    /// <summary>
    /// DeveloperFixCommands.GetAdditionalCredits walks AdditionalCredits in reverse
    /// and returns the first entry with level >= key, so without this every
    /// character above 275 looks like it has too many credits and
    /// "verify-skill-credits fix" would strip the ones we just granted.
    /// </summary>
    private static void ExtendCreditAudit(Settings settings)
    {
        var table = DeveloperFixCommands.AdditionalCredits;
        if (table is null || table.Count == 0)
            return;

        var running = table[table.Keys.Max()];

        for (var level = RetailMaxLevel + settings.SkillCreditInterval;
             level <= CurrentMaxLevel;
             level += settings.SkillCreditInterval)
        {
            if (settings.SkillCreditInterval <= 0)
                break;

            running += (int)settings.SkillCreditsPerInterval;

            if (table.ContainsKey(level))
                continue;

            table[level] = running;
            _addedCreditLevels.Add(level);
        }
    }

    private static int CreditsAdded(Settings settings) =>
        settings.SkillCreditInterval <= 0
            ? 0
            : ((CurrentMaxLevel - RetailMaxLevel) / settings.SkillCreditInterval) * (int)settings.SkillCreditsPerInterval;

    /// <summary>
    /// Puts the table back to exactly what the dat shipped, so unloading the mod
    /// leaves no trace and a reload cannot stack entries.
    /// </summary>
    public static void Revert(XpTable? table)
    {
        if (table is null || RetailMaxLevel == 0)
            return;

        var wanted = RetailMaxLevel + 1;

        if (table.CharacterLevelXPList.Count > wanted)
            table.CharacterLevelXPList.RemoveRange(wanted, table.CharacterLevelXPList.Count - wanted);

        if (table.CharacterLevelSkillCreditList.Count > wanted)
            table.CharacterLevelSkillCreditList.RemoveRange(wanted, table.CharacterLevelSkillCreditList.Count - wanted);

        foreach (var level in _addedCreditLevels)
            DeveloperFixCommands.AdditionalCredits.Remove(level);

        _addedCreditLevels.Clear();

        CurrentMaxLevel = RetailMaxLevel;
    }
}
