using System.Runtime.CompilerServices;

namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Enlightenment scaling: a copy is harder, and richer, in proportion to the
/// enlightenment of whoever owns it.
///
/// Two facts from ACE make this safe to do by editing creatures in place, and both are
/// worth stating because the obvious worry is that a buff applied to one drudge leaks
/// to every drudge on the server:
///
///   1. Skills and vitals are DEEP COPIED out of the cached weenie, unconditionally -
///      WeenieConverter.ConvertToBiota calls Clone() on each. The
///      referenceWeenieCollectionsForCommonProperties optimisation that WorldObject
///      passes as true covers PropertiesBodyPart and nothing else. So a mutation here
///      lands on one creature, in one copy.
///
///   2. None of it persists. CreatureSkill.InitLevel and CreatureVital.StartingValue
///      are the two setters on those classes that deliberately do not raise
///      ChangesDetected, and landblock spawns are not saved to the shard in the first
///      place - IsStaticThatShouldPersistToShard only answers true for statics whose
///      biota came from the database, which is houses and slumlords. This mod also
///      skips SpawnDynamicShardObjects inside copies entirely.
///
/// So a scaled monster lives and dies inside its copy, and the master landblock and
/// every other copy are untouched.
/// </summary>
public static class Scaling
{
    /// <summary>
    /// (landblock, copy) -> the enlightenment this copy was opened for.
    ///
    /// Captured when the copy is opened rather than read when a creature spawns,
    /// because Landblock.Init spawns on a BACKGROUND TASK: by the time the first drudge
    /// is being added, the Context.Assign that would have named the owner may not have
    /// run yet. Both places that open a copy record the owner before calling
    /// GetOrCreate, which is what makes the answer deterministic rather than a race.
    ///
    /// Held per copy rather than re-read per creature so that enlightening mid-run does
    /// not change the dungeon under your feet - a copy keeps the difficulty it opened
    /// with, and the next one you open is the harder one.
    /// </summary>
    private static readonly ConcurrentDictionary<(ushort Landblock, int Copy), int> _owners = new();

    /// <summary>
    /// Creatures already scaled.
    ///
    /// A guid set would be wrong here: landblock-instance guids are deterministic, so
    /// the same drudge is 0x7002B668 in every copy and they would mark each other as
    /// done. Keyed on the object itself instead, weakly, so entries leave with the
    /// creature rather than needing to be swept when a copy closes.
    ///
    /// Needed at all because AddWorldObject is not once per creature: a relocation
    /// re-adds, and without a marker the bonus would stack every time.
    /// </summary>
    private static readonly ConditionalWeakTable<WorldObject, object> _scaled = new();

    private static readonly object Marker = new();

    /// <summary>The enlightenment a copy about to be opened should be built for.</summary>
    public static void Remember(ushort landblock, int copy, Player player)
    {
        if (copy <= 0)
            return;

        _owners[(landblock, copy)] = player.Enlightenment;
    }

    public static void Forget(ushort landblock, int copy) => _owners.TryRemove((landblock, copy), out _);

    public static void ForgetAll() => _owners.Clear();

    /// <summary>What a copy was opened for, or 0 for an unowned one.</summary>
    public static int EnlightenmentOf(ushort landblock, int copy) =>
        _owners.TryGetValue((landblock, copy), out var el) ? el : 0;

    /// <summary>Enlightenments this copy is scaled by, after the cap in settings.</summary>
    public static int Counted(ushort landblock, int copy)
    {
        var el = EnlightenmentOf(landblock, copy);

        var cap = Mod.Settings.MaxEnlightenmentsCounted;

        return cap > 0 ? Math.Min(el, cap) : el;
    }

    /// <summary>
    /// The difficulty of a copy, in the words a player should see.
    ///
    /// Reports what this copy WAS BUILT WITH rather than what the settings say now.
    /// Those come apart in two ordinary ways - the owner enlightened without closing
    /// the dungeon, or the percentages were retuned while copies were open - and in
    /// both cases the monsters standing in the room are the older answer. A readout
    /// that quoted current settings would be confidently wrong exactly when somebody
    /// is asking because something looks off.
    ///
    /// Levers that are switched off are not mentioned, so this cannot promise 5% more
    /// damage from a copy where damage scaling is disabled.
    /// </summary>
    public static IEnumerable<string> Describe(ushort landblock, int copy)
    {
        var settings = Mod.Settings;

        if (!settings.ScaleWithEnlightenment || Mod.ScopeFor(landblock) != InstanceScope.Personal)
        {
            yield return "Dungeon Difficulty: Enlightenment Tier 0";
            yield return "  Unscaled - stock monsters, stock rewards.";
            yield break;
        }

        var tier = Counted(landblock, copy);

        yield return $"Dungeon Difficulty: Enlightenment Tier {tier}";

        if (tier <= 0)
        {
            yield return "  Stock monsters, stock rewards. Enlighten to raise it.";
            yield break;
        }

        var harder = new List<string>();

        if (settings.ScaleCreatureSkills) harder.Add("skills");
        if (settings.ScaleCreatureHealth) harder.Add("health");
        if (settings.ScaleCreatureDamage) harder.Add("damage");

        var richer = new List<string>();

        if (settings.ScaleExperience) richer.Add("experience");
        if (settings.ScaleLuminance) richer.Add("luminance");

        if (harder.Count > 0)
            yield return $"  Monsters: +{tier * settings.DifficultyPercentPerEnlightenment:0.##}% {Join(harder)}";

        if (richer.Count > 0)
            yield return $"  Rewards:  +{tier * settings.RewardPercentPerEnlightenment:0.##}% {Join(richer)}";

        if (harder.Count == 0 && richer.Count == 0)
            yield return "  Every scaling lever is switched off, so this copy is stock.";

        var actual = EnlightenmentOf(landblock, copy);

        if (settings.MaxEnlightenmentsCounted > 0 && actual > settings.MaxEnlightenmentsCounted)
            yield return $"  Capped at tier {settings.MaxEnlightenmentsCounted} " +
                         $"(this copy was opened at enlightenment {actual}).";
    }

    /// <summary>"skills, health and damage" - an Oxford-comma-free list for chat.</summary>
    private static string Join(List<string> parts) => parts.Count switch
    {
        0 => "",
        1 => parts[0],
        _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
    };

    /// <summary>
    /// Scales one creature on its way into a copy.
    ///
    /// Called from the AddWorldObject postfix, which covers both the initial spawn and
    /// every generator respawn during the run, since those route through
    /// LandblockManager.AddObject into the same method.
    /// </summary>
    public static void Apply(Landblock block, WorldObject wo)
    {
        var settings = Mod.Settings;

        if (!settings.ScaleWithEnlightenment)
            return;

        // Players enter through this same call. So do corpses and dropped loot.
        if (wo is not Creature creature || creature is Player || !creature.IsMonster)
            return;

        var copy = InstanceWorld.CopyOf(block);

        if (copy <= 0)
            return;

        var landblock = (ushort)((block.Id.LandblockX << 8) | block.Id.LandblockY);

        // Personal copies only, and this is a correctness rule rather than a preference,
        // so it is not a setting.
        //
        // Scaling is defined as "the enlightenment of whoever owns this copy", and only
        // a personal copy has one of those. A fellowship copy is keyed on the leader and
        // an allegiance copy on the monarch, so a shared dungeon would take its
        // difficulty from whichever member happens to hold the key - and change under
        // everyone the moment the fellowship leader passed. There is no obviously right
        // answer for a group (lowest? highest? mean?), so this declines to invent one.
        if (Mod.ScopeFor(landblock) != InstanceScope.Personal)
            return;

        var enlightenment = Counted(landblock, copy);

        if (enlightenment <= 0)
            return;

        // Idempotence before any mutation, so a re-add is a no-op rather than a second
        // helping.
        if (_scaled.TryGetValue(creature, out _))
            return;

        _scaled.Add(creature, Marker);

        var difficulty = enlightenment * settings.DifficultyPercentPerEnlightenment / 100.0;
        var reward = enlightenment * settings.RewardPercentPerEnlightenment / 100.0;

        if (settings.ScaleCreatureSkills)
            ScaleSkills(creature, difficulty);

        if (settings.ScaleCreatureHealth)
            ScaleHealth(creature, difficulty);

        if (settings.ScaleCreatureDamage)
            ScaleDamage(creature, enlightenment, settings.DifficultyPercentPerEnlightenment);

        if (settings.ScaleExperience)
            creature.XpOverride = Grow(creature.XpOverride, reward);

        if (settings.ScaleLuminance)
            creature.LuminanceAward = Grow(creature.LuminanceAward, reward);
    }

    /// <summary>
    /// Attack and defence both read GetCreatureSkill(...).Current, and for a non-player
    /// that is the attribute formula plus InitLevel plus Ranks. So a percentage of the
    /// EFFECTIVE skill added to InitLevel is what "5% better at fighting" means - taking
    /// a percentage of InitLevel alone would be a percentage of a number that is often
    /// zero on a monster weenie.
    /// </summary>
    private static void ScaleSkills(Creature creature, double difficulty)
    {
        foreach (var skill in creature.Skills.Values)
        {
            var before = skill.Current;

            if (before == 0)
                continue;

            skill.InitLevel += (uint)Math.Round(before * difficulty);
        }
    }

    /// <summary>
    /// MaxValue is StartingValue + Ranks + attribute formula, so the raise goes into
    /// StartingValue. Current has to be moved with it: the creature was built, and had
    /// its health filled to the old maximum, well before this runs.
    /// </summary>
    private static void ScaleHealth(Creature creature, double difficulty)
    {
        var health = creature.Vitals[ACE.Entity.Enum.Properties.PropertyAttribute2nd.MaxHealth];

        var before = health.MaxValue;

        if (before == 0)
            return;

        health.StartingValue += (uint)Math.Round(before * difficulty);
        health.Current = health.MaxValue;
    }

    /// <summary>
    /// Damage rating is already a percentage - GetDamageRating reads DamageRating
    /// straight off the creature for monsters, and the modifier it feeds is
    /// (100 + rating) / 100. So a rating of 5 IS five percent more damage, and the
    /// setting maps onto it one for one with no conversion.
    /// </summary>
    private static void ScaleDamage(Creature creature, int enlightenment, double percentEach)
    {
        var bonus = (int)Math.Round(enlightenment * percentEach);

        if (bonus == 0)
            return;

        creature.DamageRating = (creature.DamageRating ?? 0) + bonus;
    }

    /// <summary>
    /// Grows a reward, saturating rather than wrapping. Ten enlightenments is only
    /// +50%, but the settings are open and nothing here should turn a large reward
    /// negative because someone typed 5000.
    /// </summary>
    private static int? Grow(int? value, double reward)
    {
        if (value is not { } amount || amount <= 0)
            return value;

        var grown = amount * (1.0 + reward);

        return grown >= int.MaxValue ? int.MaxValue : (int)Math.Round(grown);
    }
}
