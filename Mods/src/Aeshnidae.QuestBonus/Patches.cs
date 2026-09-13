namespace Aeshnidae.QuestBonus;

/// <summary>
/// Keeps each character's quest-point total in sync, and applies it to XP.
///
/// All four QuestManager mutators are handled with the same prefix/postfix pair:
/// record the solve count before, compare after, and only move the total when a
/// quest crosses the 0 <-> solved boundary. Aquafir's original handled Update and
/// SetQuestCompletions this way but special-cased Decrement and Erase, where the
/// Decrement branch added points on removal instead of subtracting them. Treating
/// all four identically removes that class of bug.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    #region Quest point tracking

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update), new[] { typeof(string) })]
    public static void PreUpdate(string questFormat, QuestManager __instance, ref int __state) =>
        __state = SolvesOf(__instance, questFormat);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Update), new[] { typeof(string) })]
    public static void PostUpdate(string questFormat, QuestManager __instance, int __state) =>
        ApplyDelta(__instance, questFormat, __state);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.SetQuestCompletions), new[] { typeof(string), typeof(int) })]
    public static void PreSetQuestCompletions(string questFormat, QuestManager __instance, ref int __state) =>
        __state = SolvesOf(__instance, questFormat);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.SetQuestCompletions), new[] { typeof(string), typeof(int) })]
    public static void PostSetQuestCompletions(string questFormat, QuestManager __instance, int __state) =>
        ApplyDelta(__instance, questFormat, __state);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Decrement), new[] { typeof(string), typeof(int) })]
    public static void PreDecrement(string quest, QuestManager __instance, ref int __state) =>
        __state = SolvesOf(__instance, quest);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Decrement), new[] { typeof(string), typeof(int) })]
    public static void PostDecrement(string quest, QuestManager __instance, int __state) =>
        ApplyDelta(__instance, quest, __state);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Erase), new[] { typeof(string) })]
    public static void PreErase(string questFormat, QuestManager __instance, ref int __state) =>
        __state = SolvesOf(__instance, questFormat);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(QuestManager), nameof(QuestManager.Erase), new[] { typeof(string) })]
    public static void PostErase(string questFormat, QuestManager __instance, int __state) =>
        ApplyDelta(__instance, questFormat, __state);

    private static int SolvesOf(QuestManager manager, string questFormat)
    {
        // Only players carry a bonus; NPCs and generators have quest registries too.
        if (manager?.Creature is not Player)
            return 0;

        try
        {
            return manager.GetCurrentSolves(questFormat);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not read solves for '{questFormat}': {ex.Message}", ModManager.LogLevel.Warn);
            return 0;
        }
    }

    /// <summary>
    /// Moves the player's total only when the quest crosses unsolved &lt;-&gt; solved.
    /// Repeat completions of an already-solved quest are worth nothing, which is
    /// what keeps timers and dailies from inflating the bonus.
    /// </summary>
    private static void ApplyDelta(QuestManager manager, string questFormat, int solvesBefore)
    {
        if (manager?.Creature is not Player player)
            return;

        try
        {
            var solvesAfter = manager.GetCurrentSolves(questFormat);

            var wasSolved = solvesBefore != 0;
            var isSolved = solvesAfter != 0;

            if (wasSolved == isSolved)
                return;

            var weight = QuestBonusExtensions.WeightOf(questFormat);
            if (weight == 0)
                return;

            var delta = isSolved ? weight : -weight;
            player.AddQuestPoints(delta);

            if (Mod.Settings.NotifyQuest)
            {
                var template = isSolved ? Mod.Settings.QuestGainedMessage : Mod.Settings.QuestLostMessage;
                var message = FormatMessage(template, QuestManager.GetQuestName(questFormat), Math.Abs(delta), player);

                if (!string.IsNullOrWhiteSpace(message))
                    player.SendMessage(message);
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] quest bonus update failed for '{questFormat}': {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Fills the {quest} / {points} / {bonus} / {qp} placeholders in the message
    /// templates from Settings.json. Called after the total has been updated, so
    /// {bonus} and {qp} report the new values.
    /// </summary>
    private static string FormatMessage(string template, string questName, double points, Player player)
    {
        if (string.IsNullOrWhiteSpace(template))
            return "";

        return template
            .Replace("{quest}", questName)
            .Replace("{points}", points.ToString("0.##"))
            .Replace("{bonus}", player.QuestBonusText())
            .Replace("{qp}", player.GetQuestPoints().ToString("0.##"));
    }

    #endregion

    /// <summary>
    /// Recalculate from scratch on login, so hand-edited characters and any missed
    /// increments self-correct.
    ///
    /// Aquafir's original called CalculateQuestPoints() here and discarded the
    /// result - it is a pure sum, so the stored property was never actually
    /// refreshed. ResyncQuestPoints is the one that writes.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static void PostPlayerEnterWorld(Player __instance)
    {
        try
        {
            __instance.ResyncQuestPoints();
        }
        catch (Exception ex)
        {
            // Never throw on the login path - it would look like an ACE bug.
            ModManager.Log($"[{Mod.Name}] login resync failed for {__instance?.Name}: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Apply the multiplier to earned XP.
    ///
    /// GrantXP re-enters itself: when the player is in an XP-sharing fellowship it
    /// hands off to Fellowship.SplitXp, which calls GrantXP again for each member
    /// with ShareType.Fellowship cleared. Multiplying on the outer call as well as
    /// the inner one would square the bonus for fellowed players, so the outer call
    /// is skipped and each member's share is scaled by their own bonus instead.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.GrantXP), new[] { typeof(long), typeof(XpType), typeof(ShareType) })]
    public static void PreGrantXP(ref long amount, XpType xpType, ShareType shareType, Player __instance)
    {
        try
        {
            if (amount <= 0 || __instance is null)
                return;

            // Olthoi players earn no XP here; the call returns early.
            if (__instance.IsOlthoiPlayer)
                return;

            // About to be split across the fellowship and re-dispatched - don't
            // scale it now or the bonus applies twice.
            if (__instance.Fellowship is not null && __instance.Fellowship.ShareXP && shareType.HasFlag(ShareType.Fellowship))
                return;

            var multiplier = __instance.QuestBonusMultiplier();
            if (multiplier <= 1)
                return;

            var boosted = (long)(amount * multiplier);

            if (Mod.Settings.NotifyExp)
                __instance.SendMessage($"Quest bonus: {amount:N0} -> {boosted:N0} XP ({__instance.QuestBonusText()}).");

            amount = boosted;
        }
        catch (Exception ex)
        {
            // Leave amount untouched on failure rather than eating someone's XP.
            ModManager.Log($"[{Mod.Name}] XP boost failed for {__instance?.Name}: {ex}", ModManager.LogLevel.Error);
        }
    }
}
