namespace Aeshnidae.Enlightenment;

/// <summary>
/// Three patches, each replacing one decision ACE makes about enlightenment.
///
/// The interesting one is the emote gate. Retail's requirements are not in C# at
/// all - they are a Goto/InqIntStat state machine on the Font of Enlightenment
/// (wcid 53412), whose numbers live in two world-database rows. Left alone it
/// enforces "exactly level 275" and "enlightenment 0 to 4", so on this server it
/// would lock out anyone past 275 (Aeshnidae.MaxLevel raises the cap to 500) and
/// still cap enlightenment at five however Settings.json is configured.
///
/// The alternative was a SQL migration, and a fresh one every time a number moved.
/// Instead the chain is cut at its first instruction: the Goto that starts it is
/// intercepted, the mod answers the whole question, and on success it jumps
/// straight to the confirmation prompt the chain would have reached anyway. The
/// world database is untouched, so a world reimport cannot silently revert the
/// rules, and disabling the mod restores retail behaviour exactly.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>
    /// Cuts in ahead of the Font's requirement chain.
    ///
    /// Scoped as tightly as it can be: a Goto action, carrying the configured
    /// label, aimed at a player. Every other emote on every other creature in the
    /// world falls through on the first comparison.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(EmoteManager), nameof(EmoteManager.ExecuteEmote))]
    public static bool PreExecuteEmote(EmoteManager __instance, PropertiesEmoteAction emote,
                                       WorldObject targetObject, ref float __result)
    {
        if (!Mod.Settings.Enabled || emote is null || emote.Type != (uint)EmoteType.Goto)
            return true;

        if (!string.Equals(emote.Message, Mod.Settings.GateLabel, StringComparison.OrdinalIgnoreCase))
            return true;

        if (targetObject is not Player player)
            return true;

        // From here the mod owns the outcome, so the original never runs. __result
        // is the emote's delay in seconds; nothing below takes time.
        __result = 0f;

        try
        {
            if (Requirements.Check(player, out var failures))
            {
                __instance.ExecuteEmoteSet(EmoteCategory.GotoSet, Mod.Settings.ConfirmLabel, targetObject, true);
                return false;
            }

            foreach (var failure in failures)
                player.Session?.Network.EnqueueSend(new GameMessageSystemChat(failure, ChatMessageType.Broadcast));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] enlightenment gate failed for {player.Name}: {ex}", ModManager.LogLevel.Error);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                "The Font's judgement is clouded. Please try again shortly.", ChatMessageType.Broadcast));
        }

        return false;
    }

    /// <summary>
    /// The payload. EmoteType.Enlightenment (9001) is the only thing in ACE that
    /// calls this, and the Font's chain is the only thing that raises it, so this
    /// is the whole of the grant path.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AceEnlightenment), nameof(AceEnlightenment.HandleEnlightenment))]
    public static bool PreHandleEnlightenment(WorldObject npc, Player player)
    {
        if (!Mod.Settings.Enabled)
            return true;

        Ritual.Perform(npc, player);

        return false;
    }

    /// <summary>
    /// Anything else that asks ACE whether a player is eligible gets the mod's
    /// answer. Nothing in stock ACE calls this except HandleEnlightenment, which
    /// no longer runs - it is patched so that a future caller, or another mod,
    /// cannot end up consulting the retail rules behind this one's back.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AceEnlightenment), nameof(AceEnlightenment.VerifyRequirements))]
    public static bool PreVerifyRequirements(Player player, ref bool __result)
    {
        if (!Mod.Settings.Enabled)
            return true;

        __result = Requirements.Check(player, out var failures);

        if (!__result)
        {
            foreach (var failure in failures)
                player?.Session?.Network.EnqueueSend(new GameMessageSystemChat(failure, ChatMessageType.Broadcast));
        }

        return false;
    }

    /// <summary>Drop a departing character's re-entry state so the tables stay small.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.LogOut_Inner))]
    public static void PostLogOut(Player __instance)
    {
        if (__instance is not null)
            Guard.Forget(__instance);
    }
}
