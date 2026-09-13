namespace Aeshnidae.MaxLevel;

/// <summary>
/// ModManager.Initialize() runs at Program.cs:158, but DatManager.Initialize()
/// not until line 239 - so when this mod starts, DatManager.PortalDat is still
/// null and there is no XP table to extend yet. This postfix does the work the
/// moment the dats finish loading.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(DatManager), nameof(DatManager.Initialize),
        new[] { typeof(string), typeof(bool), typeof(bool) })]
    public static void PostDatManagerInitialize()
    {
        Mod.ApplyLevelCap("dats loaded");
    }
}
