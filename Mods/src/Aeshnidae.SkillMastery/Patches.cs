namespace Aeshnidae.SkillMastery;

/// <summary>
/// Two jobs: make the bonus real on the server, and make it visible on the client.
/// Nothing here writes mastery into a skill - see Mastery for why that matters.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>Load a character's mastery into the cache as they enter the world.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static void PostPlayerEnterWorld(Player __instance)
    {
        try
        {
            Mastery.LoadInto(__instance);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] failed to load mastery on login: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// The bonus itself. Base and Current are what every combat, magic and defence
    /// formula reads, so adding it here is what makes mastery mean anything - and
    /// because it is added on the way out rather than stored, nothing ACE does to the
    /// skill can disturb it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CreatureSkill), nameof(CreatureSkill.Base), MethodType.Getter)]
    public static void PostBase(CreatureSkill __instance, ref uint __result)
    {
        try
        {
            __result += (uint)Mastery.BonusFor(__instance);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] mastery bonus failed on Base: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Current has already had enchantments and vitae applied by the time we see it,
    /// so the bonus is scaled to match rather than added flat - see ScaledBonusFor.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CreatureSkill), nameof(CreatureSkill.Current), MethodType.Getter)]
    public static void PostCurrent(CreatureSkill __instance, ref uint __result)
    {
        try
        {
            __result += (uint)Mastery.ScaledBonusFor(__instance);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] mastery bonus failed on Current: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Display, part one: incremental skill updates.
    ///
    /// The client renders a skill as attributeFormula + InitLevel + Ranks, so the only
    /// way to show the bonus without a dat change is to include it in the InitLevel we
    /// transmit. The value is inflated for the duration of the constructor and put
    /// straight back - the message carries the bonus, the stored skill never does.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameMessagePrivateUpdateSkill), MethodType.Constructor,
        new[] { typeof(WorldObject), typeof(CreatureSkill) })]
    public static void PreUpdateSkill(CreatureSkill creatureSkill, out uint __state)
    {
        __state = creatureSkill?.InitLevel ?? 0;

        if (creatureSkill is null)
            return;

        try
        {
            var bonus = Mastery.BonusFor(creatureSkill);

            if (bonus > 0)
                creatureSkill.InitLevel = __state + (uint)bonus;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] mastery display failed on skill update: {ex}", ModManager.LogLevel.Error);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameMessagePrivateUpdateSkill), MethodType.Constructor,
        new[] { typeof(WorldObject), typeof(CreatureSkill) })]
    public static void PostUpdateSkill(CreatureSkill creatureSkill, uint __state)
    {
        if (creatureSkill is not null)
            creatureSkill.InitLevel = __state;
    }

    /// <summary>
    /// Display, part two: the full description sent at login, which writes every
    /// skill's InitLevel in one pass. Same trick, applied to all of them at once.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameEventPlayerDescription), MethodType.Constructor, new[] { typeof(Session) })]
    public static void PrePlayerDescription(Session session, out Dictionary<Skill, uint>? __state)
    {
        __state = null;

        try
        {
            var player = session?.Player;

            if (player is null)
                return;

            var saved = new Dictionary<Skill, uint>();

            foreach (Skill skill in Enum.GetValues(typeof(Skill)))
            {
                var creatureSkill = player.GetCreatureSkill(skill, false);

                if (creatureSkill is null)
                    continue;

                var bonus = Mastery.BonusFor(creatureSkill);

                if (bonus <= 0)
                    continue;

                saved[skill] = creatureSkill.InitLevel;
                creatureSkill.InitLevel += (uint)bonus;
            }

            if (saved.Count > 0)
                __state = saved;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] mastery display failed on player description: {ex}",
                           ModManager.LogLevel.Error);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameEventPlayerDescription), MethodType.Constructor, new[] { typeof(Session) })]
    public static void PostPlayerDescription(Session session, Dictionary<Skill, uint>? __state)
    {
        if (__state is null || session?.Player is null)
            return;

        foreach (var (skill, initLevel) in __state)
        {
            var creatureSkill = session.Player.GetCreatureSkill(skill, false);

            if (creatureSkill is not null)
                creatureSkill.InitLevel = initLevel;
        }
    }
}
