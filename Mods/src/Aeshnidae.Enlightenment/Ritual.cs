namespace Aeshnidae.Enlightenment;

/// <summary>
/// The enlightenment itself: what is taken, what is left alone, what is given.
///
/// This is a reimplementation of ACE's Enlightenment.HandleEnlightenment rather
/// than a set of patches around it, because almost every clause of that method is
/// one the Aeshnidae design changes. Wrapping it would mean letting it wipe society,
/// auras and the xp pool and then putting them back, which is both slower and
/// wrong - RemoveLuminance sends thirteen property updates to the client on its way
/// past, and undoing state is not the same as never touching it.
///
/// Order matters in one place: the dequip runs before the level reset, so items
/// leave the equipment slots while they are still legally wielded. Doing it the
/// other way round leaves ACE holding gear the character can no longer wield.
///
/// Nothing here writes into a CreatureSkill to preserve anything. Aeshnidae.SkillMastery
/// keeps its ranks in a shard table precisely because ResetSkill zeroes Ranks,
/// InitLevel and ExperienceSpent - mastery survives this method by not being
/// reachable from it, and the moment this file tries to help with that, it breaks.
/// </summary>
public static class Ritual
{
    /// <summary>Attribute reset certificate handed out on success.</summary>
    public const uint AttributeResetCertificate = 46421;

    public static void Perform(WorldObject npc, Player player)
    {
        if (player?.Session is null)
            return;

        if (!Guard.TryEnter(player))
        {
            ModManager.Log($"[{Mod.Name}] refused re-entrant enlightenment for {player.Name} ({player.Guid.Full:X8})",
                           ModManager.LogLevel.Warn);
            return;
        }

        var completed = false;

        try
        {
            // Before anything else: is the player still standing at the Font?
            //
            // This is the hole the old /enlighten command had. That command did the
            // reset and then sent the player to their lifestone; run it inside a
            // dungeon and the teleport was the only thing standing between them and
            // a level 1 character in high-tier content, so players ran it in portal
            // space, where the teleport could not land. They kept the location.
            //
            // Moving to an NPC removes the command but not the gap, because the
            // confirmation is asynchronous: use the Font, get the yes/no prompt,
            // recall or portal out, and *then* answer yes. The emote fires with the
            // player somewhere else entirely. So the payload refuses to run unless
            // the player is where they were when they asked.
            if (!IsStillAtTheFont(npc, player, out var reason))
            {
                ModManager.Log($"[{Mod.Name}] refused enlightenment for {player.Name} ({player.Guid.Full:X8}): {reason}",
                               ModManager.LogLevel.Warn);

                player.Session.Network.EnqueueSend(new GameMessageSystemChat(
                    "You must be standing before the Font of Enlightenment to be enlightened.",
                    ChatMessageType.Broadcast));

                return;
            }

            // Checked again here, not just at the emote gate. The gate answers a
            // question asked before the confirmation prompt; this answers it at the
            // moment of payment.
            if (!Requirements.Check(player, out var failures))
            {
                foreach (var failure in failures)
                    player.Session.Network.EnqueueSend(new GameMessageSystemChat(failure, ChatMessageType.Broadcast));

                return;
            }

            var fromLevel = player.Level ?? 0;
            var fromEnlightenment = player.Enlightenment;

            if (Mod.Settings.DequipAllItems)
                DequipAll(player);

            if (!Mod.Settings.KeepSociety)
                RemoveSociety(player);

            if (!Mod.Settings.KeepLuminanceAuras || !Mod.Settings.KeepLuminanceAccess || !Mod.Settings.KeepLuminanceBalance)
                RemoveLuminance(player);

            if (!Mod.Settings.KeepAetheria)
                RemoveAetheria(player);

            if (Mod.Settings.ResetAttributes)
                ResetAttributes(player);

            if (Mod.Settings.ResetSkills)
                ResetSkills(player);

            if (Mod.Settings.ResetLevel)
                ResetLevel(player);

            AddPerks(npc, player);

            player.SaveBiotaToDatabase();

            completed = true;

            // After the perks and the save, never interleaved with the reset above:
            // the character is dequipped and mid-change until here.
            Ceremony(npc, player);

            // Audited unconditionally. An enlightenment is the single largest state
            // change a character can undergo, and "it says here you were level 275"
            // is the only way to answer a support ticket about one.
            ModManager.Log($"[{Mod.Name}] {player.Name} ({player.Guid.Full:X8}) enlightened " +
                           $"{fromEnlightenment} -> {player.Enlightenment} from level {fromLevel}");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] enlightenment failed for {player.Name}: {ex}", ModManager.LogLevel.Error);

            player.Session?.Network.EnqueueSend(new GameMessageSystemChat(
                "Something went wrong during your enlightenment. Please contact an administrator before continuing.",
                ChatMessageType.Broadcast));
        }
        finally
        {
            Guard.Exit(player, completed);
        }
    }

    /// <summary>
    /// Four conditions, cheapest first. Each one on its own is enough to make the
    /// grant unsafe, and none of them is a setting - a ritual that fires while the
    /// player is mid-teleport is a bug however the server is configured.
    /// </summary>
    private static bool IsStillAtTheFont(WorldObject npc, Player player, out string reason)
    {
        reason = null!;

        // Portal space. The player exists but is attached to no landblock, which is
        // exactly the state the old exploit was run from.
        if (player.CurrentLandblock is null)
        {
            reason = "player is in portal space";
            return false;
        }

        if (player.Teleporting)
        {
            reason = "player is mid-teleport";
            return false;
        }

        if (!Mod.Settings.RequireProximityToNpc)
            return true;

        // The Font is a fixed world object, so a missing landblock or a different
        // one means the player left after opening the prompt.
        if (npc?.CurrentLandblock is null || npc.CurrentLandblock != player.CurrentLandblock)
        {
            reason = "player is no longer in the Font's landblock";
            return false;
        }

        if (!player.IsWithinUseRadiusOf(npc, Mod.Settings.ProximityRadius))
        {
            reason = $"player is more than {Mod.Settings.ProximityRadius:N1}m from the Font";
            return false;
        }

        return true;
    }

    // ---- taking -------------------------------------------------------------

    private static void DequipAll(Player player)
    {
        foreach (var equipped in player.EquippedObjects.Keys.ToList())
            player.HandleActionPutItemInContainer(equipped.Full, player.Guid.Full, 0);
    }

    /// <summary>
    /// Retail's society wipe, kept for completeness but off by default. The
    /// "Enlightened&lt;Society&gt;Master" stamps are what let the promotions officer
    /// restore the rank in one conversation, so they go in even here.
    /// </summary>
    private static void RemoveSociety(Player player)
    {
        player.QuestManager.Erase("SocietyMember");
        player.QuestManager.Erase("CelestialHandMember");
        player.QuestManager.Erase("EnlightenedCelestialHandMaster");
        player.QuestManager.Erase("EldrytchWebMember");
        player.QuestManager.Erase("EnlightenedEldrytchWebMaster");
        player.QuestManager.Erase("RadiantBloodMember");
        player.QuestManager.Erase("EnlightenedRadiantBloodMaster");

        if (player.SocietyRankCelhan == 1001)
            player.QuestManager.Stamp("EnlightenedCelestialHandMaster");
        if (player.SocietyRankEldweb == 1001)
            player.QuestManager.Stamp("EnlightenedEldrytchWebMaster");
        if (player.SocietyRankRadblo == 1001)
            player.QuestManager.Stamp("EnlightenedRadiantBloodMaster");

        player.Faction1Bits = null;
        Send(player, PropertyInt.Faction1Bits, 0);

        player.SocietyRankCelhan = null;
        Send(player, PropertyInt.SocietyRankCelhan, 0);
        player.SocietyRankEldweb = null;
        Send(player, PropertyInt.SocietyRankEldweb, 0);
        player.SocietyRankRadblo = null;
        Send(player, PropertyInt.SocietyRankRadblo, 0);
    }

    /// <summary>
    /// Split three ways, because the design keeps the auras and the earning right
    /// while retail took both. Each block is guarded independently so an admin can
    /// mix them.
    /// </summary>
    private static void RemoveLuminance(Player player)
    {
        if (!Mod.Settings.KeepLuminanceAccess)
        {
            player.QuestManager.Erase("OracleLuminanceRewardsAccess_1110");
            player.QuestManager.Erase("LoyalToShadeOfLadyAdja");
            player.QuestManager.Erase("LoyalToKahiri");
            player.QuestManager.Erase("LoyalToLiamOfGelid");
            player.QuestManager.Erase("LoyalToLordTyragar");
        }

        if (!Mod.Settings.KeepLuminanceAuras)
        {
            player.LumAugDamageRating = 0;
            Send(player, PropertyInt.LumAugDamageRating, 0);
            player.LumAugDamageReductionRating = 0;
            Send(player, PropertyInt.LumAugDamageReductionRating, 0);
            player.LumAugCritDamageRating = 0;
            Send(player, PropertyInt.LumAugCritDamageRating, 0);
            player.LumAugCritReductionRating = 0;
            Send(player, PropertyInt.LumAugCritReductionRating, 0);
            player.LumAugSurgeChanceRating = 0;
            Send(player, PropertyInt.LumAugSurgeChanceRating, 0);
            player.LumAugItemManaUsage = 0;
            Send(player, PropertyInt.LumAugItemManaUsage, 0);
            player.LumAugItemManaGain = 0;
            Send(player, PropertyInt.LumAugItemManaGain, 0);

            // Not zeroed: LumAugVitality. ACE derives the +2 per enlightenment from
            // PropertyInt.Enlightenment in CreatureVital and leaves this at whatever
            // auras set it, so clearing it here would take vitality the player bought
            // rather than vitality enlightenment gave.
            player.LumAugHealingRating = 0;
            Send(player, PropertyInt.LumAugHealingRating, 0);
            player.LumAugSkilledCraft = 0;
            Send(player, PropertyInt.LumAugSkilledCraft, 0);
            player.LumAugSkilledSpec = 0;
            Send(player, PropertyInt.LumAugSkilledSpec, 0);
            player.LumAugAllSkills = 0;
            Send(player, PropertyInt.LumAugAllSkills, 0);
        }

        if (!Mod.Settings.KeepLuminanceBalance)
        {
            player.AvailableLuminance = null;
            Send64(player, PropertyInt64.AvailableLuminance, 0);
            player.MaximumLuminance = null;
            Send64(player, PropertyInt64.MaximumLuminance, 0);
        }

        player.SendMessage("Your Luminance fades from your spirit.", ChatMessageType.Broadcast);
    }

    private static void RemoveAetheria(Player player)
    {
        foreach (var tier in new[] { "EFUL", "EFML", "EFLL" })
        {
            foreach (var field in new[] { "North", "South", "East", "West", "Center" })
                player.QuestManager.Erase($"{tier}{field}ManaFieldUsed");
        }

        player.AetheriaFlags = AetheriaBitfield.None;
        Send(player, PropertyInt.AetheriaBitfield, 0);

        player.SendMessage("Your mastery of Aetheric magics fades.", ChatMessageType.Broadcast);
    }

    private static void ResetAttributes(Player player)
    {
        var count = Enum.GetNames(typeof(PropertyAttribute)).Length;

        for (var i = 1; i < count; i++)
        {
            var attribute = (PropertyAttribute)i;

            player.Attributes[attribute].Ranks = 0;
            player.Attributes[attribute].ExperienceSpent = 0;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdateAttribute(player, player.Attributes[attribute]));
        }

        // Vitals step by two: the enum interleaves current and max for each vital,
        // and only the max entries are trainable.
        count = Enum.GetNames(typeof(PropertyAttribute2nd)).Length;

        for (var i = 1; i < count; i += 2)
        {
            var vital = (PropertyAttribute2nd)i;

            player.Vitals[vital].Ranks = 0;
            player.Vitals[vital].ExperienceSpent = 0;
            player.Session.Network.EnqueueSend(new GameMessagePrivateUpdateVital(player, player.Vitals[vital]));
        }

        player.SendMessage("Your attribute training fades.", ChatMessageType.Broadcast);
    }

    private static void ResetSkills(Player player)
    {
        var count = Enum.GetNames(typeof(Skill)).Length;

        for (var i = 1; i < count; i++)
            player.ResetSkill((Skill)i, false);

        if (!Mod.Settings.KeepUnassignedExperience)
        {
            player.AvailableExperience = 0;
            Send64(player, PropertyInt64.AvailableExperience, 0);
        }

        // Skill credits are recomputed rather than decremented: the character is
        // about to be level 1, and ACE re-grants the level-based credits from
        // CharacterLevelSkillCreditList on the way back up. Only the permanent
        // sources belong here. (Aeshnidae.MaxLevel's credits past 275 are level
        // based, so they come back with the levels.)
        //
        // ACE reads the heritage group unguarded here. This does not, because by
        // this point the skills are already reset: throwing would leave a character
        // stripped and un-credited, which is the one failure mode worth an extra
        // three lines. A null heritage should not be reachable on a level 275.
        if (player.Heritage is null ||
            !DatManager.PortalDat.CharGen.HeritageGroups.TryGetValue((uint)player.Heritage, out var heritage))
        {
            ModManager.Log($"[{Mod.Name}] {player.Name} has no usable heritage group " +
                           $"({player.Heritage?.ToString() ?? "null"}); skill credits left untouched, fix by hand",
                           ModManager.LogLevel.Error);
            return;
        }

        var credits = (int)heritage.SkillCredits
                    + player.QuestManager.GetCurrentSolves("ArantahKill1")
                    + player.QuestManager.GetCurrentSolves("OswaldManualCompleted")
                    + player.QuestManager.GetCurrentSolves("LumAugSkillQuest");

        player.AvailableSkillCredits = credits;
        Send(player, PropertyInt.AvailableSkillCredits, credits);

        player.SendMessage("Your available skill credits have been adjusted.", ChatMessageType.Broadcast);
    }

    private static void ResetLevel(Player player)
    {
        player.TotalExperience = 0;
        Send64(player, PropertyInt64.TotalExperience, 0);

        player.Level = 1;
        Send(player, PropertyInt.Level, 1);
    }

    // ---- giving -------------------------------------------------------------

    /// <summary>
    /// The showpiece. Three beats: the Font wakes and the world goes white for the
    /// player alone, with the chant; the augmentation burst on the character for
    /// everyone to see; then the fireworks, and the white lifts.
    ///
    /// Runs on the world thread already (we are inside an emote), so the chain is for
    /// the timing, not the threading. The fog is the one thing here that can go wrong:
    /// it is sticky per player, across landblocks and a relog, so it is set through
    /// SetFogColor - which tracks it - and cleared on the same chain, unconditionally.
    /// SendEnvironChange would paint it without tracking it, and ClearFogColor would
    /// then decline to clear what it did not know about.
    /// </summary>
    private static void Ceremony(WorldObject npc, Player player)
    {
        if (!Mod.Settings.Ceremony)
            return;

        try
        {
            var chain = new ActionChain();

            // Each beat guards itself: the action queue has no catch of its own, and a
            // player who logs out between beats has no session and no landblock. The
            // last beat clears the fog even so - a stuck white screen is the one
            // failure that follows a player home.
            chain.AddAction(player, () => Beat(player, () =>
            {
                if (npc?.CurrentLandblock is not null)
                    npc.ApplyVisualEffects(PlayScript.EnchantUpWhite);

                player.SetFogColor(EnvironChangeType.WhiteFog);
                player.SendEnvironChange(EnvironChangeType.Chant1Sound);
            }));

            chain.AddDelaySeconds(1.2);
            chain.AddAction(player, () => Beat(player, () => player.ApplyVisualEffects(PlayScript.AugmentationUseOther)));

            chain.AddDelaySeconds(1.2);
            chain.AddAction(player, () =>
            {
                Beat(player, () => player.ApplyVisualEffects(PlayScript.WeddingBliss));

                try { if (player?.Session is not null) player.ClearFogColor(); } catch { }
            });

            chain.EnqueueChain();
        }
        catch (Exception ex)
        {
            // The fog is the only thing worth a second attempt - a failed particle is
            // nothing, a stuck white screen is a support ticket.
            try { player.ClearFogColor(); } catch { }

            ModManager.Log($"[{Mod.Name}] ceremony failed for {player.Name}: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    private static void Beat(Player player, Action act)
    {
        try
        {
            if (player?.Session is null || player.CurrentLandblock is null)
                return;

            act();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] ceremony beat failed: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// The +1 to all trained skills and +2 vitality are not applied here. ACE reads
    /// PropertyInt.Enlightenment directly in CreatureSkill.Base and CreatureVital,
    /// so incrementing the property below is what grants them - and it grants them
    /// to every skill, retroactively, without anything being written into a skill.
    /// </summary>
    private static void AddPerks(WorldObject npc, Player player)
    {
        player.Enlightenment += 1;
        Send(player, PropertyInt.Enlightenment, player.Enlightenment);

        player.SendMessage("You have become enlightened and view the world with new eyes.", ChatMessageType.Broadcast);
        player.SendMessage("You have risen to a higher tier of enlightenment!", ChatMessageType.Broadcast);

        if (Mod.Settings.GrantTitles)
            AddTitle(player);

        if (Mod.Settings.GrantAttributeResetCertificate && npc is not null)
            player.GiveFromEmote(npc, AttributeResetCertificate, 1);

        if (!Mod.Settings.BroadcastToServer)
            return;

        var message = $"{player.Name} has achieved the {Requirements.Ordinal(player.Enlightenment)} level of Enlightenment!";

        PlayerManager.BroadcastToAll(new GameMessageSystemChat(message, ChatMessageType.WorldBroadcast));
        PlayerManager.LogBroadcastChat(Channel.AllBroadcast, null, message);
    }

    /// <summary>
    /// Five titles exist in the client's dats and no more, so a sixth enlightenment
    /// is untitled rather than mistitled. Deliberately silent - the design caps
    /// titles at five while leaving enlightenment itself uncapped.
    /// </summary>
    private static void AddTitle(Player player)
    {
        var title = player.Enlightenment switch
        {
            1 => CharacterTitle.Awakened,
            2 => CharacterTitle.Enlightened,
            3 => CharacterTitle.Illuminated,
            4 => CharacterTitle.Transcended,
            5 => CharacterTitle.CosmicConscious,
            _ => (CharacterTitle?)null,
        };

        if (title is not null)
            player.AddTitle(title.Value);
    }

    // ---- plumbing -----------------------------------------------------------

    private static void Send(Player player, PropertyInt property, int value) =>
        player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt(player, property, value));

    private static void Send64(Player player, PropertyInt64 property, long value) =>
        player.Session.Network.EnqueueSend(new GameMessagePrivateUpdatePropertyInt64(player, property, value));
}
