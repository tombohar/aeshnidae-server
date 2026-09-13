namespace Aeshnidae.Fellowship;

[HarmonyPatch]
public static class Patches
{
    private static readonly AccessTools.FieldRef<AceFellowship, Dictionary<uint, Player>> _membersOf =
        AccessTools.FieldRefAccess<AceFellowship, Dictionary<uint, Player>>("FellowshipMembers");

    /// <summary>
    /// Replaces ACE's hardcoded share table with the configured one.
    ///
    /// ACE's version is a switch on member count that stops at 9 and falls through to
    /// 1.0, with a TODO in the source asking what should happen for larger fellowships.
    /// That fallthrough is why raising MaxFellows on its own is a bug: a tenth member
    /// would take every fellow from a .3 share to a full one.
    ///
    /// A prefix rather than a postfix, because the point is to replace the answer
    /// rather than adjust it - and if the settings have nothing to say, ACE's own
    /// number is used unchanged.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AceFellowship), "GetMemberSharePercent")]
    public static bool PreGetMemberSharePercent(AceFellowship __instance, ref double __result)
    {
        try
        {
            if (!Mod.Settings.Enabled)
                return true;

            var members = _membersOf(__instance)?.Count ?? 0;

            if (members <= 0)
                return true;   // nothing sensible to say; let ACE answer

            __result = Mod.Settings.ShareFor(members);
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] share lookup failed, using ACE's table: {ex}",
                           ModManager.LogLevel.Error);
            return true;
        }
    }
}
