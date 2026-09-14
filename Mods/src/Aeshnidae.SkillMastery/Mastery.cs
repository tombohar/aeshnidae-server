namespace Aeshnidae.SkillMastery;

/// <summary>
/// Mastery is computed, never stored on the skill.
///
/// The first cut of this wrote the bonus into CreatureSkill.InitLevel, which the
/// client already renders. That was wrong: TrainSkill, SpecializeSkill, UntrainSkill,
/// UnspecializeSkill, ResetSkill and AugmentationDevice all bare-assign InitLevel, so
/// a player who enlightened and then re-trained had the bonus silently wiped a moment
/// after it was restored. Chasing six writers with six postfixes would have held only
/// until ACE added a seventh.
///
/// So nothing of ours goes into the skill at all. Base and Current are postfixed to
/// add the bonus on the way out, and the two serializers that send skills to the
/// client are patched to include it in what they transmit. ACE can assign InitLevel
/// as freely as it likes - we are not in its way and it is not in ours, and
/// enlightenment stops being a special case entirely.
///
/// Base and Current are read constantly during combat, so the lookup is served from
/// an in-memory cache and never touches the database.
/// </summary>
public static class Mastery
{
    /// <summary>character guid -> skill -> mastery ranks. Loaded at login.</summary>
    private static readonly ConcurrentDictionary<uint, Dictionary<Skill, int>> _cache = new();

    private static readonly AccessTools.FieldRef<CreatureSkill, Creature> _creatureOf =
        AccessTools.FieldRefAccess<CreatureSkill, Creature>("creature");

    /// <summary>Skill points granted by a rank count - whole points only.</summary>
    public static int SkillPointsFor(int ranks) =>
        Mod.Settings.RanksPerSkillPoint < 1 ? ranks : ranks / Mod.Settings.RanksPerSkillPoint;

    /// <summary>
    /// A rank count as points with the fraction showing: 37 ranks at ten per point is
    /// "3.7". With one rank per point it is just the number, so the readouts read the
    /// same whichever way the server is set.
    /// </summary>
    public static string Points(int ranks)
    {
        var per = Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        if (per == 1)
            return ranks.ToString("N0");

        return $"{ranks / per:N0}.{ranks % per}";
    }

    /// <summary>What one rank is called in chat: "a point" or "a tenth of a point".</summary>
    public static string RankWord()
    {
        var per = Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        return per switch
        {
            1 => "a point",
            10 => "a tenth of a point",
            _ => $"1/{per} of a point",
        };
    }

    public static void LoadInto(Player player)
    {
        if (!MasteryDb.Ready)
            return;

        var rows = MasteryDb.Load(player.Guid.Full);
        var skills = new Dictionary<Skill, int>();

        foreach (var (skill, ranks) in rows)
        {
            if (ranks > 0)
                skills[skill] = ranks;
        }

        _cache[player.Guid.Full] = skills;
    }

    public static int RanksOf(Player player, Skill skill) =>
        _cache.TryGetValue(player.Guid.Full, out var skills) && skills.TryGetValue(skill, out var ranks)
            ? ranks
            : 0;

    private static void SetRanks(Player player, Skill skill, int ranks)
    {
        var skills = _cache.GetOrAdd(player.Guid.Full, _ => new Dictionary<Skill, int>());

        lock (skills)
            skills[skill] = ranks;

        MasteryDb.Save(player.Guid.Full, skill, ranks);
    }

    /// <summary>
    /// The skill points mastery is currently contributing. Zero unless the skill is
    /// trained or specialized - mastery in a skill you have dropped is banked, not
    /// spent, and comes back the moment you train it again.
    /// </summary>
    public static int BonusFor(CreatureSkill creatureSkill)
    {
        if (!Mod.Settings.Enabled || creatureSkill is null)
            return 0;

        if (creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
            return 0;

        if (_creatureOf(creatureSkill) is not Player player)
            return 0;

        if (!_cache.TryGetValue(player.Guid.Full, out var skills))
            return 0;

        lock (skills)
            return skills.TryGetValue(creatureSkill.Skill, out var ranks) ? SkillPointsFor(ranks) : 0;
    }

    /// <summary>
    /// The mastery bonus as it should land on Current, scaled by the same enchantment
    /// multiplier and vitae that retail ranks get.
    ///
    /// CreatureSkill.Current multiplies its base by enchantments and vitae before we
    /// ever see the result, so a flat postfix would leave mastery unbuffable - strictly
    /// worse than an equivalent retail rank for anyone with a buff up, which is most
    /// people most of the time. Mastery already pays a heavy tax in RanksPerSkillPoint;
    /// it should not pay a second one by being excluded from buffs.
    ///
    /// Applying the same factors here is arithmetically equal to injecting the bonus
    /// into the base, without needing a transpiler. The one difference is rounding:
    /// ACE rounds its total once at the end and this adds a separately rounded term, so
    /// the result can sit a single point away from a true inline injection.
    /// </summary>
    public static int ScaledBonusFor(CreatureSkill creatureSkill)
    {
        var bonus = BonusFor(creatureSkill);

        if (bonus <= 0)
            return 0;

        var creature = _creatureOf(creatureSkill);

        if (creature is null)
            return bonus;

        var multiplier = creature.EnchantmentManager?.GetSkillMod_Multiplier(creatureSkill.Skill) ?? 1.0f;

        // Vitae is a death penalty below 1.0; retail ranks are scaled by it, so mastery is too.
        var vitae = creature is Player player && player.Vitae != 1.0f ? player.Vitae : 1.0f;

        return (int)Math.Max(0, Math.Round(bonus * multiplier * vitae));
    }

    /// <summary>
    /// The last retail rank's price for this advancement class - what a
    /// CostPerSkillPoint of 1.0 means. Read from the dat so it stays right if the
    /// table ever changes underneath us.
    /// </summary>
    public static long BaseRankCost(SkillAdvancementClass advancementClass)
    {
        var table = DatManager.PortalDat?.XpTable;

        if (table is null)
            return 0;

        var list = advancementClass == SkillAdvancementClass.Specialized
            ? table.SpecializedSkillXpList
            : table.TrainedSkillXpList;

        return list.Count < 2 ? 0 : list[^1] - list[^2];
    }

    /// <summary>
    /// Price of the next rank, given how many skill points are already held. Growth
    /// compounds only from the band's own start, so editing one band never shifts the
    /// ones after it.
    ///
    /// One price for every skill unless PriceByAdvancementClass says otherwise: the
    /// ranks bought are class-agnostic (BonusFor applies them to a trained or a
    /// specialized skill alike), so a price that depended on the class at the moment
    /// of purchase could be dodged by ordering the purchases.
    /// </summary>
    public static long CostOfNextRank(int currentRanks, SkillAdvancementClass advancementClass)
    {
        var points = SkillPointsFor(currentRanks);
        var band = Mod.Settings.BandFor(points);
        var priceClass = Mod.Settings.PriceByAdvancementClass ? advancementClass : SkillAdvancementClass.Trained;
        var baseCost = BaseRankCost(priceClass);

        // The band prices a whole skill point; split it across the ranks that make
        // one up, so RanksPerSkillPoint changes granularity and never total cost.
        var cost = baseCost * band.CostPerSkillPoint / Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        if (band.Growth is > 0 and not 1.0)
            cost *= Math.Pow(band.Growth, points - Mod.Settings.BandStart(band));

        return (long)Math.Max(1, Math.Round(cost));
    }

    /// <summary>
    /// Total price of buying <paramref name="count"/> ranks from where the character
    /// stands. Priced rank by rank, so a purchase that crosses a band boundary pays
    /// the right price on each side of it.
    /// </summary>
    public static long CostOfRanks(int currentRanks, int count, SkillAdvancementClass advancementClass)
    {
        long total = 0;

        for (var i = 0; i < count; i++)
            total += CostOfNextRank(currentRanks + i, advancementClass);

        return total;
    }

    /// <summary>
    /// Buys ranks with the account's banked Radiance. Returns a message either
    /// way - this is only ever called from a command, and the player wants to know why
    /// nothing happened as much as they want to know when it worked.
    /// </summary>
    public static bool Raise(Player player, Skill skill, int count, out string message)
    {
        message = "";

        if (!Mod.Settings.Enabled)
        {
            message = "Skill mastery is turned off on this server.";
            return false;
        }

        if (!MasteryDb.Ready)
        {
            message = $"Mastery storage is unavailable: {MasteryDb.LastError}";
            return false;
        }

        if (count < 1)
        {
            message = "Raise by at least one rank.";
            return false;
        }

        var creatureSkill = player.GetCreatureSkill(skill, false);

        if (creatureSkill is null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
        {
            message = $"You must be trained in {skill.ToSentence()} before you can master it.";
            return false;
        }

        if (Mod.Settings.RequireRetailCap && !creatureSkill.IsMaxRank)
        {
            message = $"{skill.ToSentence()} is not at its retail maximum yet - raise it the normal way first.";
            return false;
        }

        var currentRanks = RanksOf(player, skill);
        var maxRanks = Mod.Settings.MaxSkillPoints * Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        if (currentRanks + count > maxRanks)
        {
            message = $"That would pass the mastery ceiling of {Mod.Settings.MaxSkillPoints:N0} skill points.";
            return false;
        }

        var cost = CostOfRanks(currentRanks, count, creatureSkill.AdvancementClass);

        if (player.Account is null)
        {
            message = "Mastery is paid for in Radiance from your account bank, and this character has no account.";
            return false;
        }

        // Radiance, from the account's bank balance. The debit is guarded in SQL, so the
        // balance check here is for the message; the UPDATE is what actually decides.
        var accountId = player.Account.AccountId;
        var available = MasteryDb.RadianceBalance(accountId);

        if (cost > available)
        {
            message = $"{skill.ToSentence()} needs {cost:N0} Radiance for {count} rank(s); your account has {available:N0} banked. " +
                      "(Radiance earned in the last few seconds may not be banked yet.)";
            return false;
        }

        if (!MasteryDb.TryDebitRadiance(accountId, cost, out var remaining))
        {
            message = "That could not be paid for - your Radiance balance changed while the purchase was being made. Try again.";
            return false;
        }

        var newRanks = currentRanks + count;

        SetRanks(player, skill, newRanks);

        // Re-send the skill so the client picks up the new total straight away.
        player.Session?.Network.EnqueueSend(new GameMessagePrivateUpdateSkill(player, creatureSkill));

        var before = SkillPointsFor(currentRanks);
        var after = SkillPointsFor(newRanks);
        var perPoint = Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        message = $"Your {skill.ToSentence()} mastery rises to {Points(newRanks)} skill points, for {cost:N0} Radiance ({remaining:N0} left).";

        if (perPoint > 1)
        {
            var toNext = perPoint - newRanks % perPoint;

            message += after > before
                ? $" That is +{after:N0} on the skill now."
                : $" The skill shows +{after:N0} until the next whole point - {toNext} more raise{(toNext == 1 ? "" : "s")}.";
        }

        return true;
    }
}
