namespace Aeshnidae.MoveGuard;

/// <summary>
/// Three patches. The prefix is the guard; the postfix is how it learns which
/// positions ACE actually accepted; the logout hook is housekeeping.
///
/// The prefix never throws into ACE: a failure inside the guard is logged and the
/// move is allowed, because a bug here must degrade to stock behaviour rather than
/// freeze every player on the shard.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    public static bool UpdatePlayerPosition_Prefix(Player __instance, Position newPosition, bool forceUpdate, ref bool __result)
    {
        try
        {
            return Guard.Judge(__instance, newPosition, forceUpdate, ref __result);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] guard failed for {__instance?.Name}, move allowed: {ex}", ModManager.LogLevel.Error);
            return true;
        }
    }

    /// <summary>
    /// UpdatePlayerPosition's return value means "changed landblock", not "accepted",
    /// so acceptance is read off the side effect instead: on success ACE assigns
    /// <c>Location = newPosition</c>, the very same object. A rejected move - ours or
    /// ACE's own - leaves Location as it was and the clock where it was.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.UpdatePlayerPosition))]
    public static void UpdatePlayerPosition_Postfix(Player __instance, Position newPosition)
    {
        try
        {
            if (newPosition is not null && ReferenceEquals(__instance.Location, newPosition))
                Tracker.For(__instance).LastAccepted = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] postfix failed for {__instance?.Name}: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.SwitchPlayerFromOnlineToOffline))]
    public static void Logout_Postfix(Player player)
    {
        try
        {
            if (player is not null)
                Tracker.Forget(player);
        }
        catch { /* housekeeping only */ }
    }
}
