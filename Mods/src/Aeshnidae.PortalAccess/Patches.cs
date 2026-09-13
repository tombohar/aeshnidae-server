namespace Aeshnidae.PortalAccess;

/// <summary>
/// Removes portal level requirements at the source rather than at the refusal.
///
/// The obvious patch is a postfix on Portal.CheckUseRequirements that turns
/// "you are not powerful enough" into success. That is wrong, and quietly so: the
/// level test sits near the top of that method and returns early, so converting
/// its failure to success also skips every check below it - PK and Olthoi
/// restrictions, vitae, account age, quest requirements. A portal that was both
/// level gated and PK gated would stop being PK gated.
///
/// Patching MinLevel and MaxLevel instead means CheckUseRequirements runs in full
/// and simply finds no level requirement to enforce, so every other restriction
/// still applies exactly as before.
///
/// The properties are declared on WorldObject, not Portal, and housing shares them -
/// a slumlord's MinLevel is the level needed to buy the dwelling. Hence the type
/// test: only portals are affected, and housing is untouched.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>
    /// Null rather than zero. The call site is <c>player.Level &lt; MinLevel</c> on
    /// two nullable ints, and any comparison with null is false - which is the
    /// same answer ACE gives for a portal that never had a requirement.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.MinLevel), MethodType.Getter)]
    public static void PostMinLevel(WorldObject __instance, ref int? __result)
    {
        if (Mod.Settings.RemoveMinLevel && __instance is Portal)
            __result = null;
    }

    /// <summary>
    /// The maximum has a second switch upstream - the server property
    /// use_portal_max_level_requirement - so this is belt and braces. It is here
    /// so both halves of "portals no longer care what level you are" live in one
    /// place, and can be answered by one line of Settings.json.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(WorldObject), nameof(WorldObject.MaxLevel), MethodType.Getter)]
    public static void PostMaxLevel(WorldObject __instance, ref int? __result)
    {
        if (Mod.Settings.RemoveMaxLevel && __instance is Portal)
            __result = null;
    }
}
