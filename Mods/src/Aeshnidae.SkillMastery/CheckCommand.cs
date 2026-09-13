namespace Aeshnidae.SkillMastery;

/// <summary>
/// Breaks a skill down into everything that feeds it, so a number can be explained
/// rather than guessed at.
///
/// Two rules keep this honest. Components are derived arithmetically from ACE's own
/// Base and Current rather than recomputed, so they always sum correctly. And the
/// final effective figure for a defence is taken from Creature.GetEffectiveDefenseSkill
/// itself - the same call the combat system makes - so what this prints is what you
/// are actually defending with, not this mod's opinion of it.
/// </summary>
public static class CheckCommand
{
    [CommandHandler("check", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
        "Show what a skill actually works out to, and where each part of it comes from.",
        "/check <skill>")]
    public static void HandleCheck(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null || parameters.Length == 0)
            return;

        var name = string.Join(" ", parameters);

        if (!TryParseSkill(name, out var skill))
        {
            Reply(session, $"No skill called '{name}'.");
            return;
        }

        var creatureSkill = player.GetCreatureSkill(skill, false);

        if (creatureSkill is null)
        {
            Reply(session, $"You have no {skill.ToSentence()} skill.");
            return;
        }

        var mastery = Mastery.BonusFor(creatureSkill);
        var augBase = creatureSkill.GetAugBonus_Base(player);

        var baseTotal = creatureSkill.Base;      // includes mastery, via our postfix
        var current = creatureSkill.Current;     // base, then enchantments and vitae

        // Whatever Base holds that is not ranks, init, augs or mastery is the attribute
        // contribution - derived rather than recomputed so the column always adds up.
        var attributes = Math.Max(0, (long)baseTotal - creatureSkill.Ranks - creatureSkill.InitLevel - augBase - mastery);

        var sb = new StringBuilder();

        sb.AppendLine($"{skill.ToSentence()} ({creatureSkill.AdvancementClass})");
        sb.AppendLine($"  attributes      : {attributes,10:N0}");
        sb.AppendLine($"  retail ranks    : {creatureSkill.Ranks,10:N0}");

        if (creatureSkill.InitLevel > 0)
            sb.AppendLine($"  init/spec bonus : {creatureSkill.InitLevel,10:N0}");

        if (augBase > 0)
            sb.AppendLine($"  augmentations   : {augBase,10:N0}");

        var heldRanks = Mastery.RanksOf(player, skill);
        var partial = heldRanks % Math.Max(1, Mod.Settings.RanksPerSkillPoint) != 0;

        sb.AppendLine($"  mastery         : {mastery,10:N0}" +
                      (creatureSkill.AdvancementClass < SkillAdvancementClass.Trained
                          ? "  (dormant - not trained)"
                          : partial ? $"  ({Mastery.Points(heldRanks)} held; whole points count)" : ""));

        sb.AppendLine($"  base            : {baseTotal,10:N0}");
        sb.AppendLine($"  buffs/debuffs   : {(long)current - baseTotal,+10:N0}" +
                      (baseTotal > 0 ? $"  (x{(double)current / baseTotal:0.000})" : ""));
        sb.AppendLine($"  current         : {current,10:N0}");

        AppendCombat(sb, player, skill, current);

        Reply(session, sb.ToString().TrimEnd());
    }

    /// <summary>
    /// The combat layer, which lives outside CreatureSkill entirely and is therefore
    /// the part people forget. Each branch mirrors ACE's own function and then prints
    /// that function's result, so the total is never this mod's arithmetic:
    ///
    ///   melee/missile defence  Round(Current * weapon * burden * stance + imbues)
    ///   magic defence          Round(Current * weapon + imbues)
    ///   attack                 Round(Current * accuracy * offense)
    /// </summary>
    private static void AppendCombat(StringBuilder sb, Player player, Skill skill, uint current)
    {
        if (skill is Skill.MeleeDefense or Skill.MissileDefense)
        {
            var combatType = skill == Skill.MissileDefense ? CombatType.Missile : CombatType.Melee;

            var weaponMod = combatType == CombatType.Missile
                ? WorldObject.GetWeaponMissileDefenseModifier(player)
                : WorldObject.GetWeaponMeleeDefenseModifier(player);

            var imbueType = combatType == CombatType.Missile
                ? ImbuedEffectType.MissileDefense
                : ImbuedEffectType.MeleeDefense;

            sb.AppendLine("  --- in combat ---");
            sb.AppendLine($"  weapon          : {"x" + weaponMod.ToString("0.000"),10}");
            sb.AppendLine($"  burden          : {"x" + player.GetBurdenMod().ToString("0.000"),10}");
            sb.AppendLine($"  stance          : {"x" + player.GetDefenseStanceMod().ToString("0.000"),10}");
            sb.AppendLine($"  armour imbues   : {player.GetDefenseImbues(imbueType),+10:N0}");
            sb.AppendLine($"  EFFECTIVE       : {player.GetEffectiveDefenseSkill(combatType),10:N0}");

            AppendCaveats(sb, player);
            return;
        }

        if (skill == Skill.MagicDefense)
        {
            sb.AppendLine("  --- in combat ---");
            sb.AppendLine($"  weapon          : {"x" + WorldObject.GetWeaponMagicDefenseModifier(player).ToString("0.000"),10}");
            sb.AppendLine($"  armour imbues   : {player.GetDefenseImbues(ImbuedEffectType.MagicDefense),+10:N0}");
            sb.AppendLine($"  EFFECTIVE       : {player.GetEffectiveMagicDefense(),10:N0}");

            AppendCaveats(sb, player);
            return;
        }

        // Attack. ACE composes this from whatever weapon is actually equipped, so the
        // figure is only meaningful for the skill that weapon uses.
        var currentWeaponSkill = player.GetCurrentWeaponSkill();

        if (skill != currentWeaponSkill)
        {
            sb.Append($"  (equip a {skill.ToSentence()} weapon to see its effective attack; " +
                      $"currently wielding {currentWeaponSkill.ToSentence()})");
            return;
        }

        var offenseMod = WorldObject.GetWeaponOffenseModifier(player);
        var effective = player.GetEffectiveAttackSkill();

        sb.AppendLine("  --- in combat ---");
        sb.AppendLine($"  weapon offense  : {"x" + offenseMod.ToString("0.000"),10}");

        // Accuracy is derived rather than read, so it always reconciles the two numbers.
        if (current > 0 && offenseMod > 0)
            sb.AppendLine($"  accuracy        : {"x" + (effective / (current * offenseMod)).ToString("0.000"),10}");

        sb.AppendLine($"  EFFECTIVE       : {effective,10:N0}");

        AppendCaveats(sb, player);
    }

    private static void AppendCaveats(StringBuilder sb, Player player)
    {
        if (player.CombatMode == CombatMode.NonCombat)
            sb.AppendLine("  (out of combat - weapon modifiers do not apply until you draw)");

        if (player.IsExhausted)
            sb.AppendLine("  (exhausted)");
    }

    private static bool TryParseSkill(string name, out Skill skill)
    {
        var cleaned = name.Replace(" ", "").Replace("_", "");

        foreach (Skill candidate in Enum.GetValues(typeof(Skill)))
        {
            if (candidate.ToString().Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            {
                skill = candidate;
                return true;
            }
        }

        skill = Skill.None;
        return false;
    }

    /// <summary>
    /// One chat message per line, with any carriage return stripped.
    ///
    /// StringBuilder.AppendLine emits Environment.NewLine, which is CR LF on Windows.
    /// The client breaks the line on LF and renders the stray CR as an unmapped glyph -
    /// those are the musical notes. Every other mod here splits the same way; this is
    /// the house pattern, not a local workaround.
    /// </summary>
    private static void Reply(Session session, string message)
    {
        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
