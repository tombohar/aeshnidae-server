namespace Aeshnidae.Enlightenment;

/// <summary>
/// Makes a character eligible to enlighten, for testing. It does not enlighten them.
///
/// The distinction is the whole point of this file. The Font is the only thing that
/// may grant an enlightenment (see Commands.cs and Guard.cs for why), and nothing
/// here touches PropertyInt.Enlightenment. What this does is stand a test character
/// at the door with every prerequisite met: the level the Font asks for, every aura
/// at its cap, and Master rank in the Celestial Hand. They still have to walk up to
/// the Font, get the prompt, and say yes - which is exactly the path being tested.
///
/// Every call is logged with who ran it and on whom. It is Admin-only and it is loud.
/// </summary>
public static class Prep
{
    public static string Run(Player admin, Player target)
    {
        var settings = Mod.Settings;
        var sb = new StringBuilder();

        sb.AppendLine($"Preparing {target.Name} for enlightenment #{target.Enlightenment + 1}:");

        // ---- level ----------------------------------------------------------
        var requiredLevel = Requirements.LevelFor(target);
        var level = target.Level ?? 1;

        if (level < requiredLevel)
        {
            var table = DatManager.PortalDat.XpTable.CharacterLevelXPList;

            if (requiredLevel >= table.Count)
            {
                sb.AppendLine($"  level    : cannot raise to {requiredLevel} - the XP table stops at {table.Count - 1}. " +
                              "Raise Aeshnidae.MaxLevel or lower the requirement.");
            }
            else
            {
                var delta = (long)table[requiredLevel] - (target.TotalExperience ?? 0);

                if (delta > 0)
                    RaiseLevel(target, delta);

                sb.AppendLine($"  level    : {level} -> {requiredLevel}  (+{delta:N0} xp, admin type, unscaled)");
            }
        }
        else
        {
            sb.AppendLine($"  level    : {level}, already >= {requiredLevel}");
        }

        // ---- auras ----------------------------------------------------------
        if (settings.RequireAllLuminanceAuras)
        {
            var raised = new List<string>();

            foreach (var (name, property, _, max) in Requirements.Auras)
            {
                var current = target.GetProperty(property) ?? 0;

                if (current >= max)
                    continue;

                target.SetProperty(property, max);
                target.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(target, property, max));
                raised.Add($"{name} {current}->{max}");
            }

            sb.AppendLine(raised.Count == 0
                ? "  auras    : all already at cap"
                : $"  auras    : {string.Join(", ", raised)}");
        }

        // ---- society --------------------------------------------------------
        if (settings.RequireSocietyMaster)
        {
            if (Requirements.IsSocietyMaster(target))
            {
                sb.AppendLine("  society  : already a Master");
            }
            else
            {
                MakeCelestialHandMaster(target);
                sb.AppendLine("  society  : Celestial Hand Master (rank 1001)");
            }
        }

        // ---- the one thing this cannot do ---------------------------------------
        if (settings.RequiredFreeInventorySlots > 0)
        {
            var free = target.GetFreeInventorySlots();

            sb.AppendLine(free >= settings.RequiredFreeInventorySlots
                ? $"  pack     : {free} free slots, ok"
                : $"  pack     : {free} free slots, NEEDS {settings.RequiredFreeInventorySlots} - clear some space by hand");
        }

        target.SaveBiotaToDatabase();

        ModManager.Log($"[{Mod.Name}] PREP by {admin.Name} ({admin.Guid.Full:X8}) on {target.Name} ({target.Guid.Full:X8}): " +
                       $"level {level}->{Math.Max(level, requiredLevel)}, auras capped, society master",
                       ModManager.LogLevel.Warn);

        sb.AppendLine();
        sb.AppendLine("Prerequisites applied. Enlightenment itself is not - use the Font for that.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Straight into ACE's private UpdateXpAndLevel rather than through GrantXP.
    ///
    /// GrantXP is where Aeshnidae.QuestBonus applies its multiplier, to every XpType
    /// including Admin, so an exact delta through it lands somewhere above the target
    /// level. UpdateXpAndLevel is the step after that prefix: it adds the xp, runs
    /// CheckForLevelup, grants skill credits per level, and sends the client updates -
    /// everything a real level-up does, minus the bonus.
    ///
    /// Run on the player's own action queue, as GrantXP itself does.
    /// </summary>
    private static void RaiseLevel(Player target, long delta)
    {
        var method = AccessTools.Method(typeof(Player), "UpdateXpAndLevel", new[] { typeof(long), typeof(XpType) });

        if (method is null)
            throw new MissingMethodException("Player.UpdateXpAndLevel(long, XpType) not found - ACE changed; fix Prep.RaiseLevel");

        target.EnqueueAction(new ActionEventDelegate(() => method.Invoke(target, new object[] { delta, XpType.Admin })));
    }

    /// <summary>
    /// The same sequence ACE's own /faction ch 5 runs, so the character is a Master
    /// by every test the world applies - the rank property, the faction bits, and
    /// the quest flags society NPCs branch on.
    /// </summary>
    private static void MakeCelestialHandMaster(Player target)
    {
        target.Faction1Bits = FactionBits.CelestialHand;
        target.SocietyRankCelhan = 1001;
        target.SocietyRankEldweb = null;
        target.SocietyRankRadblo = null;

        target.QuestManager.SetQuestBits("SocietyMember", (int)FactionBits.CelestialHand, true);
        target.QuestManager.SetQuestBits("SocietyFlag", (int)FactionBits.CelestialHand, true);
        target.QuestManager.SetQuestBits("SocietyMember", (int)(FactionBits.EldrytchWeb | FactionBits.RadiantBlood), false);
        target.QuestManager.SetQuestBits("SocietyFlag", (int)(FactionBits.EldrytchWeb | FactionBits.RadiantBlood), false);
        target.QuestManager.Stamp("CelestialHandMember");
        target.QuestManager.Erase("EldrytchWebMember");
        target.QuestManager.Erase("RadiantBloodMember");

        target.Session.Network.EnqueueSend(
            new GameMessagePrivateUpdatePropertyInt(target, PropertyInt.Faction1Bits, (int)FactionBits.CelestialHand),
            new GameMessagePrivateUpdatePropertyInt(target, PropertyInt.SocietyRankCelhan, 1001),
            new GameMessagePrivateUpdatePropertyInt(target, PropertyInt.SocietyRankEldweb, 0),
            new GameMessagePrivateUpdatePropertyInt(target, PropertyInt.SocietyRankRadblo, 0));
    }
}
