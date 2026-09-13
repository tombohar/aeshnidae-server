namespace Aeshnidae.Bank;

/// <summary>
/// The two faucets: monster kills pay radiance, quest completions pay resonance.
///
/// Both are postfixes that observe rather than replace. Nothing here changes what ACE
/// awards, so a mod failure costs the currency and nothing else - experience, luminance
/// and quest state are untouched either way.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    // ------------------------------------------------------------------ radiance

    /// <summary>
    /// Radiance is experience's mirror: one Radiance per point of experience received,
    /// from everything experience comes from.
    ///
    /// Hooked on Player.GrantXP because it is the one funnel every award passes through
    /// AFTER the modifiers have been applied - a kill (EarnXP applies xp_modifier and the
    /// enchantment), a quest emote (quest_xp_modifier too), a level-proportional award,
    /// and each member's slice of a fellowship split. What lands on the experience bar is
    /// what lands in Radiance, and nothing has to re-derive damage shares or quest rules.
    ///
    /// The one deliberate divergence: UpdateXpAndLevel stops adding experience at the
    /// level cap, and this runs before that check. A capped character keeps earning
    /// Radiance. That is the whole point of it existing.
    ///
    /// What is NOT counted, and why:
    ///   - The outer call of a fellowship split. GrantXP with ShareType.Fellowship set
    ///     hands the whole amount to Fellowship.SplitXp, which re-enters GrantXP once per
    ///     member without the flag. Counting the outer call too would pay the sharer for
    ///     everyone's slice. The early-return condition is mirrored exactly.
    ///   - Allegiance passup. A vassal's kill already paid them; the patron's share of it
    ///     arrives as XpType.Allegiance and would be the same kill counted twice.
    ///   - Proficiency, Emote, Admin. Skill-use trickle, the AwardSkillXP emote's pass
    ///     through the pool, and /grantxp are not "earned" in the sense a player means.
    ///   - Olthoi. GrantXP itself gives them no experience; they get no Radiance either.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.GrantXP), new[] { typeof(long), typeof(XpType), typeof(ShareType) })]
    public static void PostGrantXP(Player __instance, long amount, XpType xpType, ShareType shareType)
    {
        if (!Mod.Settings.RadianceEnabled || !BankDb.Ready || amount <= 0)
            return;

        if (xpType is not (XpType.Kill or XpType.Quest))
            return;

        if (__instance.IsOlthoiPlayer || __instance.Account is null)
            return;

        // The outer call of a split does no granting itself - skip it, as the original did.
        if (__instance.Fellowship != null && __instance.Fellowship.ShareXP && shareType.HasFlag(ShareType.Fellowship))
            return;

        try
        {
            var radiance = (long)Math.Round(amount * Mod.Settings.RadiancePerExperience);

            if (radiance <= 0)
                return;

            Earning.Award(__instance.Account.AccountId, CurrencyKind.Radiance, radiance);
            History.Record(__instance, CurrencyKind.Radiance, radiance);
            Earning.AnnounceRadiance(__instance, radiance);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not award radiance to {__instance?.Name ?? "?"}: {ex}",
                           ModManager.LogLevel.Error);
        }
    }

    // ----------------------------------------------------------------- luminance

    /// <summary>
    /// Where luminance goes, and how much of it /earned counts.
    ///
    /// Hooked on the private AddLuminance rather than the public EarnLuminance because
    /// that is the single point where a balance actually moves: EarnLuminance applies
    /// the server modifiers and GrantLuminance may hand the whole thing to
    /// Fellowship.SplitLuminance, which re-enters per member. Watching the outer call
    /// would credit the sharer with everybody's luminance and the sharees with none.
    ///
    /// For a FLAGGED character the award is diverted to the bank and the original is
    /// skipped entirely, so nothing reaches AvailableLuminance and nothing can be lost
    /// to MaximumLuminance on the way. That is the whole point of the flag: a capped
    /// character stops earning luminance, and a flagged one never caps.
    ///
    /// XpType.Admin runs normally either way. The bank pays withdrawals out through that
    /// path, so counting them would make taking your own luminance out of the bank look
    /// like income - and diverting them would make it impossible to take out at all.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), "AddLuminance")]
    public static bool PreAddLuminance(Player __instance, long amount, XpType xpType, out long __state)
    {
        __state = __instance.AvailableLuminance ?? 0;

        if (xpType is XpType.Admin || amount <= 0)
            return true;

        if (!AutoBank.IsOn(__instance) || __instance.Account is null || !BankDb.Ready)
            return true;

        try
        {
            Earning.Award(__instance.Account.AccountId, CurrencyKind.Luminance, amount);
            History.Record(__instance, CurrencyKind.Luminance, amount);

            if (Mod.Settings.AnnounceLuminanceBanked)
                __instance.SendMessage($"{amount:N0} Luminance banked.");

            return false;   // skip the original; it never touches the character
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not auto-bank luminance for {__instance.Name}, " +
                           $"letting it land normally: {ex}", ModManager.LogLevel.Error);
            return true;
        }
    }

    /// <summary>
    /// Records what an UNFLAGGED character actually gained - the real change in the
    /// balance, not the amount awarded. AddLuminance clamps to MaximumLuminance and
    /// silently discards the remainder, so a capped player is genuinely earning nothing
    /// however much is being thrown at them, and /earned showing a row of zeroes is the
    /// correct answer rather than a bug. A flagged character never reaches here with a
    /// change to measure, because the prefix already banked it and skipped the original.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), "AddLuminance")]
    public static void PostAddLuminance(Player __instance, XpType xpType, long __state)
    {
        if (xpType is XpType.Admin)
            return;

        try
        {
            var gained = (__instance.AvailableLuminance ?? 0) - __state;

            if (gained > 0)
                History.Record(__instance, CurrencyKind.Luminance, gained);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not record luminance for {__instance?.Name ?? "?"}: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    // ------------------------------------------------------- spending from the bank

    /// <summary>
    /// Lets a flagged character spend banked luminance, by drawing the shortfall onto
    /// them first and then letting ACE's own SpendLuminance do exactly what it always
    /// does.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.SpendLuminance))]
    public static void PreSpendLuminance(Player __instance, long amount)
    {
        if (amount <= 0 || !AutoBank.IsOn(__instance))
            return;

        AutoBank.TopUp(__instance, amount);
    }

    /// <summary>
    /// The half of spending that is easy to miss.
    ///
    /// Aura NPCs do not simply call SpendLuminance and see whether it worked - the emote
    /// ignores its return value entirely. They gate the sale first with InqInt64Stat on
    /// AvailableLuminance, which reads the property straight off the character. So for a
    /// flagged character with everything banked, the NPC would decide they could not
    /// afford it and never reach the spend at all: auto-banking would quietly lock them
    /// out of buying auras, which is the one thing luminance is for.
    ///
    /// Topping up before the check means the property the NPC reads is true by the time
    /// it reads it, and the purchase then proceeds through entirely stock code.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote))]
    public static void PreExecuteEmote(PropertiesEmoteAction emote, WorldObject targetObject)
    {
        if (emote is null || emote.Type != (uint)EmoteType.InqInt64Stat)
            return;

        if (emote.Stat != (int)PropertyInt64.AvailableLuminance)
            return;

        if (targetObject is not Player player || !AutoBank.IsOn(player))
            return;

        try
        {
            // Min64 is the floor the emote is testing for - the price. Nothing to do for
            // a test with no floor, which is not asking whether they can afford anything.
            if (emote.Min64 is { } needed && needed > 0)
                AutoBank.TopUp(player, needed);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not prepare banked luminance for {player.Name}: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    // ------------------------------------------------------------------- session

    /// <summary>
    /// Starts and ends the /earned session clock.
    ///
    /// Tied to login rather than to the first kill so that the hourly rate counts the
    /// whole session - the travel, the buffing, the corpse runs. Starting the clock on
    /// the first award instead would quietly measure only the productive part and
    /// report a rate nobody actually sustains.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static void PostEnterWorld(Player __instance)
    {
        History.StartSession(__instance);

        // Read once here so the luminance hot path only ever reads a dictionary. Login
        // already touches the database heavily; a landblock tick must not.
        AutoBank.Load(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    public static void PostLogOut(Player __instance)
    {
        History.EndSession(__instance);
        AutoBank.Forget(__instance);
    }

    // ----------------------------------------------------------------- resonance

    /// <summary>
    /// Resonance, on any increase in a quest's completion count.
    ///
    /// Solves are counted before and after rather than the postfix simply reading the
    /// new value, because the registry has no "this was just completed" signal - Update
    /// both creates and increments, and the only way to know a completion happened is
    /// that the number went up. This is the same before/after shape Aeshnidae.QuestBonus
    /// uses on the same method, and for the same reason.
    ///
    /// Unlike QuestBonus, which pays only on the first solve so dailies cannot inflate a
    /// permanent bonus, this pays on EVERY increase by default: a currency should pay
    /// for repeatable content, that is what makes repeatable content worth doing. Set
    /// ResonanceFirstSolveOnly if you would rather it did not.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update), new[] { typeof(string) })]
    public static void PreQuestUpdate(string questFormat, QuestManager __instance, ref int __state) =>
        __state = SolvesOf(__instance, questFormat);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update), new[] { typeof(string) })]
    public static void PostQuestUpdate(string questFormat, QuestManager __instance, int __state) =>
        AwardResonance(__instance, questFormat, __state);

    private static int SolvesOf(QuestManager manager, string questFormat)
    {
        // NPCs and generators keep quest registries too, and only players have accounts.
        if (manager?.Creature is not Player)
            return 0;

        try
        {
            return manager.GetCurrentSolves(questFormat);
        }
        catch
        {
            return 0;
        }
    }

    private static void AwardResonance(QuestManager manager, string questFormat, int solvesBefore)
    {
        if (!Mod.Settings.ResonanceEnabled || !BankDb.Ready)
            return;

        if (manager?.Creature is not Player player || player.Account is null)
            return;

        try
        {
            var solvesAfter = manager.GetCurrentSolves(questFormat);

            if (solvesAfter <= solvesBefore)
                return;

            if (Mod.Settings.ResonanceFirstSolveOnly && solvesBefore != 0)
                return;

            var name = QuestManager.GetQuestName(questFormat);

            var resonance = Earning.ResonanceFor(name);

            if (resonance <= 0)
                return;

            Earning.Award(player.Account.AccountId, CurrencyKind.Resonance, resonance);
            History.Record(player, CurrencyKind.Resonance, resonance);

            if (Mod.Settings.AnnounceResonance)
                player.SendMessage($"You gain {resonance:N0} Resonance.");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not award resonance for '{questFormat}': {ex}",
                           ModManager.LogLevel.Error);
        }
    }
}
