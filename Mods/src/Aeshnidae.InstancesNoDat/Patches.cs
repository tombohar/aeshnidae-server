namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// The routing. Five patches, and every one of them declines to act unless a copy is
/// actually in play - so with no instances open the server behaves exactly as stock.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>
    /// Our copies are not in ACE's landblocks[,] array, so nothing would ever tick
    /// them and an instance would sit frozen - no creature AI, no physics, no
    /// generators. Driving them straight after ACE drives its own keeps the two in
    /// step within a single server tick.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.Tick))]
    public static void PostTick(double portalYearTicks)
    {
        try
        {
            InstanceWorld.Tick(portalYearTicks);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] instance tick failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Allocates and opens a copy for a player, used by both entry paths. Failure is
    /// never fatal: the player simply arrives on the server's own landblock, which is
    /// the same geometry, just shared.
    /// </summary>
    private static void EnsureCopy(Player player, ushort landblock, string why)
    {
        // Idempotent, and group-aware: if this player's fellowship or allegiance already
        // holds a copy here, that is the answer. Every caller happens to check this
        // first today, but the one that forgot to split a fellowship on every reload.
        var existing = Context.CopyFor(player, landblock);

        if (existing > 0)
        {
            Context.Assign(player, landblock, existing, Mod.ScopeFor(landblock));
            return;
        }

        var copy = InstanceWorld.AllocateCopy(landblock, Mod.Settings.MaxCopiesPerLandblock);

        if (copy == 0)
        {
            player.SendMessage($"Every copy of this place is occupied " +
                               $"({Mod.Settings.MaxCopiesPerLandblock} in use). Try again shortly.");
            return;
        }

        // Before GetOrCreate, not after. Landblock.Init spawns on a background task, so
        // the first monster can be built before the Context.Assign below has run - and
        // scaling that asked "whose copy is this" at spawn time would sometimes get no
        // answer and hand out a stock dungeon. Recording it here closes that window.
        Scaling.Remember(landblock, copy, player);

        if (InstanceWorld.GetOrCreate(landblock, copy) is null)
            return;

        var scope = Mod.ScopeFor(landblock);

        Context.Assign(player, landblock, copy, scope);

        ModManager.Log($"[{Mod.Name}] {player.Name} {why} copy {copy} of {landblock:X4} " +
                       $"({scope}, shared with {Context.GroupDescription(player, scope)})");
    }

    /// <summary>
    /// Entry. Every way into a landblock ends here - a portal, a recall, a summon, an
    /// admin teleport - so hooking Teleport rather than the portal means nothing else
    /// has to know instances exist. Flag a dungeon in PersonalLandblocks and every
    /// existing portal into it starts handing out private copies, with no new weenie
    /// and no world database change.
    ///
    /// A player already assigned to this landblock keeps their copy, so stepping out
    /// and back returns you to your own dungeon rather than a fresh one.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.Teleport), new[] { typeof(Position), typeof(bool) })]
    public static void PreTeleport(Player __instance, Position _newPosition)
    {
        try
        {
            if (!Mod.Settings.Enabled || _newPosition is null)
                return;

            var landblock = (ushort)(_newPosition.LandblockId.Raw >> 16);

            if (!Mod.IsInstanced(landblock))
                return;

            if (Context.HasForced(__instance, landblock, out _))
                return;   // a destination was named on purpose - do not hand out a copy

            if (Context.CopyFor(__instance, landblock) > 0)
                return;   // already have a copy here - go back to it

            EnsureCopy(__instance, landblock, "entering");
        }
        catch (Exception ex)
        {
            // A failed assignment must never block the teleport - the player simply
            // arrives on the server's own landblock instead.
            ModManager.Log($"[{Mod.Name}] personal copy assignment failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Placing an object into the world. This is the one moment a copy is chosen
    /// rather than inherited, so a player's assignment decides it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.AddObject))]
    public static bool PreAddObject(WorldObject worldObject, bool loadAdjacents, ref bool __result)
    {
        try
        {
            if (worldObject?.Location is null)
                return true;

            var landblock = (ushort)(worldObject.Location.LandblockId.Raw >> 16);

            // A player arriving with no assignment is logging in - their position was
            // saved inside a copy that no longer exists. These dungeons have no public
            // version to fall back to, so give them a fresh copy rather than dropping
            // them into the shared landblock.
            if (worldObject is Player arriving && Mod.Settings.Enabled
                && Mod.IsInstanced(landblock) && !Mod.IsStaff(arriving)
                && Context.CopyFor(arriving, landblock) == 0)
            {
                EnsureCopy(arriving, landblock, "logging in");
            }

            var copy = Context.Resolve(worldObject, landblock);

            if (copy <= 0)
            {
                // The case that eats an afternoon: /createinst spawns through
                // EnterWorld -> AddObject, and a brand new creature is not a player and
                // has no landblock yet, so nothing about it says which copy it belongs
                // in. It goes to the master, which is right - the master is the one
                // authored version - but that is NOT where the dev is standing if they
                // are in a copy, so the thing they just made appears to have vanished.
                if (worldObject is not Player && worldObject.ProjectileSource is null
                    && InstanceWorld.OpenCount(landblock) > 0)
                {
                    Mod.Trace($"{worldObject.Name} spawned into the MASTER of {landblock:X4}; " +
                              $"{InstanceWorld.OpenCount(landblock)} copy(ies) are open and none of " +
                              "them has it. Authored objects reach copies on /inst reload.");
                }

                return true;   // not instanced - let ACE do exactly what it always did
            }

            var block = InstanceWorld.GetOrCreate(landblock, copy);

            if (block is null)
                return true;

            Mod.Trace($"{worldObject.Name} added to copy {copy} of {landblock:X4}");

            __result = block.AddWorldObject(worldObject);
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] AddObject routing failed, falling back to stock: {ex}",
                           ModManager.LogLevel.Error);
            return true;
        }
    }

    /// <summary>
    /// Physics moved something across a landblock boundary. The rule that carries this
    /// whole design: an object bound to a copy stays in that copy. Without it, the
    /// first step a player took inside an instance would silently relocate them into
    /// ACE's original landblock.
    ///
    /// The question is asked of the DESTINATION, never of where the object came from.
    /// An earlier version bailed out unless the object was already standing in one of
    /// our copies, which guarded the way out of an instance and left the way in wide
    /// open: walk out of a dungeon and back in on foot - not a portal, so no teleport
    /// to hook - and ACE's own path put you on the shared landblock while your
    /// assignment still said copy 2. The copy you thought you were in then sat empty,
    /// the janitor closed it on schedule, and its creatures died on a client that was
    /// never told, leaving a drudge standing there forever. Origin tells you nothing
    /// about where something belongs; only the destination and the assignment do.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.RelocateObjectForPhysics))]
    public static bool PreRelocate(WorldObject worldObject, bool adjacencyMove)
    {
        try
        {
            if (worldObject?.Location is null)
                return true;

            var oldBlock = worldObject.CurrentLandblock;
            var landblock = (ushort)(worldObject.Location.LandblockId.Raw >> 16);
            var copy = Context.Resolve(worldObject, landblock);

            if (copy <= 0 && worldObject is Player entering && Mod.Settings.Enabled
                && Mod.IsInstanced(landblock) && !Mod.IsStaff(entering)
                && !Context.HasForced(entering, landblock, out _))
            {
                // Walking into an instanced dungeon with no assignment. The login path
                // already hands out a copy in this situation; this is the same case
                // arriving on foot, and without it the player simply stands in the
                // shared landblock. These dungeons are not meant to have a public
                // version, so "no assignment" must mean "give them one", not "let them
                // into the original".
                //
                // An assignment lives only in memory, so a mod reload empties the table
                // and every player already inside an instance falls into exactly this
                // case on their next crossing.
                EnsureCopy(entering, landblock, "walking into");

                copy = Context.Resolve(worldObject, landblock);
            }

            if (copy <= 0)
            {
                // Leaving a copy needs the same full re-entry that entering one does,
                // and ACE's stock path cannot give it: it passes the adjacencyMove flag
                // it was handed, and when that is true the physics object is never
                // destroyed, so CurCell still points into the copy's cells and
                // AddWorldObjectInternal skips AddPhysicsObj entirely.
                //
                // The result is the exact mirror of the bug on the way in: the player is
                // listed in the master, /instance says the master, the master genuinely
                // holds the objects - and they see an empty room, because their physics
                // is still in the cells of the copy they left. Half-fixing this was why
                // a drudge that every check agreed was there could not be seen.
                //
                // Orphaned blocks count too. Those are the worst case: nothing ticks
                // them, so a player whose physics is stranded in one sees a room frozen
                // at the moment it was dropped.
                if (InstanceWorld.IsOurs(oldBlock) || InstanceWorld.IsOrphaned(oldBlock))
                {
                    var previous = Context.Ambient;
                    Context.Ambient = null;   // we want ACE's own landblock, not a copy

                    Landblock destination;

                    try
                    {
                        destination = LandblockManager.GetLandblock(worldObject.Location.LandblockId, true);
                    }
                    finally
                    {
                        Context.Ambient = previous;
                    }

                    if (destination is null)
                        return true;

                    if (worldObject is Player)
                        Mod.Trace($"{worldObject.Name} left copy {InstanceWorld.CopyOf(oldBlock)} " +
                                  $"of {landblock:X4} for the server's own landblock");

                    oldBlock!.RemoveWorldObjectForPhysics(worldObject.Guid, adjacencyMove: false);
                    destination.AddWorldObject(worldObject);
                    return false;
                }

                // Nothing to do with instances at all - ACE's path is correct.
                return true;
            }

            var newBlock = InstanceWorld.Find(landblock, copy);

            if (newBlock is null)
            {
                // A live assignment pointing at a copy that does not exist. Close()
                // clears assignments, so this should be unreachable - but the failure
                // mode is a player silently dropped into the shared dungeon, which is
                // too quiet a way to lose an instance. Say so.
                ModManager.Log($"[{Mod.Name}] {worldObject.Name} is assigned to copy {copy} of " +
                               $"{landblock:X4}, which is not open - falling back to the shared landblock",
                               ModManager.LogLevel.Warn);
                return true;
            }

            if (ReferenceEquals(oldBlock, newBlock))
            {
                // Moving within the same copy. ACE would remove and re-add across
                // landblocks here; within one landblock there is nothing to do.
                return false;
            }

            if (worldObject is Player)
            {
                var from = InstanceWorld.CopyOf(oldBlock);

                Mod.Trace($"{worldObject.Name} crossed into copy {copy} of {landblock:X4} from " +
                          (from > 0 ? $"copy {from}" : "the master or outside the dungeon"));
            }

            // adjacencyMove: false, deliberately, whatever ACE passed in.
            //
            // This is the difference between being in a copy and only being listed in
            // one. Landblock.AddWorldObjectInternal re-files an object into its new
            // landblock's physics cells ONLY when PhysicsObj.CurCell is null:
            //
            //     if (wo.PhysicsObj.CurCell == null)
            //         var success = wo.AddPhysicsObj();
            //
            // and CurCell is only cleared by PhysicsObj.DestroyObject(), which the
            // removal skips when adjacencyMove is true. So passing ACE's flag through
            // put the player in the destination copy's player list while their physics
            // object stayed in the cells of the copy they left: /instance reported the
            // new copy, and they could not see anyone in it. Two players who both
            // believed they were in copy 2 were rendering different dungeons.
            //
            // An adjacency move is a step between two neighbouring landblocks that share
            // a cell space. Two copies never do, however identical their ids, so a copy
            // change is by definition not adjacent - it is a full re-entry, and the
            // clients of the copy being left need telling as much.
            //
            // Null oldBlock once the inbound path was opened up: an object arriving from
            // outdoors has a landblock, one arriving from nowhere does not.
            oldBlock?.RemoveWorldObjectForPhysics(worldObject.Guid, adjacencyMove: false);

            newBlock.AddWorldObject(worldObject);
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] relocation routing failed, falling back to stock: {ex}",
                           ModManager.LogLevel.Error);
            return true;
        }
    }


    /// <summary>
    /// The other half of /teleto: bringing a player TO the admin.
    ///
    /// /teletome teleports the target to the admin's position, and a position cannot tell
    /// two copies apart - so the target arrived with no assignment, was treated as
    /// somebody walking in fresh, and was handed a private copy of their own. An admin
    /// summoning someone got a player who could not see them, standing in a different
    /// dungeon; an admin standing OUTSIDE the dungeon got a player who appeared to have
    /// been thrown out of it.
    ///
    /// The target goes wherever the admin is, master included - being summoned means
    /// being put next to the person who summoned you.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ACE.Server.Command.Handlers.AdminCommands),
                  nameof(ACE.Server.Command.Handlers.AdminCommands.HandleTeleToMe))]
    public static void PreTeleToMe(Session session, string[] parameters)
    {
        try
        {
            if (!Mod.Settings.Enabled || session?.Player?.Location is null
                || parameters is null || parameters.Length == 0)
                return;

            var landblock = (ushort)(session.Player.Location.LandblockId.Raw >> 16);

            if (!Mod.IsInstanced(landblock))
                return;   // summoning them out of an instance needs nothing from us

            var target = PlayerManager.GetOnlinePlayer(string.Join(" ", parameters));

            if (target is null)
                return;

            var copy = InstanceWorld.CopyOf(session.Player.CurrentLandblock);

            Context.Assign(target, landblock, copy, Mod.ScopeFor(landblock));
            Context.Force(target, landblock, copy);

            Mod.Trace($"{target.Name} summoned into " +
                      (copy > 0 ? $"copy {copy}" : "the master") + $" of {landblock:X4} by {session.Player.Name}");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not summon a player into this copy: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// Makes /teleto work on someone inside an instance.
    ///
    /// ACE teleports the admin to the target's Position, and a Position cannot tell two
    /// copies of a dungeon apart - so the admin arrives in a private copy of the same
    /// geometry, alone, which is the opposite of what /teleto is for. Someone checking
    /// on a player would conclude they had vanished.
    ///
    /// Patching /teleto rather than only offering /inst goto matters because /teleto is
    /// what an admin will actually type under pressure.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ACE.Server.Command.Handlers.AdminCommands),
                  nameof(ACE.Server.Command.Handlers.AdminCommands.HandleTeleto))]
    public static void PreTeleto(Session session, string[] parameters)
    {
        try
        {
            if (!Mod.Settings.Enabled || session?.Player is null || parameters is null || parameters.Length == 0)
                return;

            var target = PlayerManager.GetOnlinePlayer(string.Join(" ", parameters));

            if (target?.Location is null)
                return;

            var landblock = (ushort)(target.Location.LandblockId.Raw >> 16);

            if (!Mod.IsInstanced(landblock))
                return;

            var copy = InstanceWorld.CopyOf(target.CurrentLandblock);

            Context.Assign(session.Player, landblock, copy, Mod.ScopeFor(landblock));

            // Beats the stay-put rule, which would otherwise keep an admin who is
            // already in some other copy of this dungeon exactly where they are.
            Context.Force(session.Player, landblock, copy);
        }
        catch (Exception ex)
        {
            // Never block a teleport an admin asked for; they simply arrive in their own
            // copy, which is the behaviour they had before this patch existed.
            ModManager.Log($"[{Mod.Name}] could not follow a player into their copy: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// Keeps a copy's static objects out of ACE's global guid registry, and out of each
    /// other's way. See Registry for why they collide at all.
    ///
    /// Skipping the original rather than adding alongside it is the point: if a copy
    /// wrote to the global slot it would evict whatever was there, which is precisely
    /// how a drudge authored in the master ended up existing only in someone's copy.
    /// The master keeps the global slot; copies keep their own.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ServerObjectManager), nameof(ServerObjectManager.AddServerObject))]
    public static bool PreAddServerObject(PhysicsObj obj)
    {
        try
        {
            return !Registry.Add(obj);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] registry add failed, using ACE's: {ex.Message}",
                           ModManager.LogLevel.Warn);
            return true;
        }
    }

    /// <summary>
    /// The mirror. A copy tearing down its own object must not remove the master's
    /// registration, which is what a guid-keyed global remove does.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ServerObjectManager), nameof(ServerObjectManager.RemoveServerObject))]
    public static bool PreRemoveServerObject(PhysicsObj obj)
    {
        try
        {
            return !Registry.Remove(obj);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] registry remove failed, using ACE's: {ex.Message}",
                           ModManager.LogLevel.Warn);
            return true;
        }
    }

    /// <summary>
    /// Answers a guid lookup with the object from the copy being processed, when there
    /// is one. With no ambient copy this declines to act, so every lookup outside an
    /// instance returns exactly what it always did.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ServerObjectManager), nameof(ServerObjectManager.GetObjectA))]
    public static bool PreGetObjectA(uint objectID, ref PhysicsObj __result)
    {
        try
        {
            var mine = Registry.Get(objectID);

            if (mine is null)
                return true;

            __result = mine;
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] registry lookup failed, using ACE's: {ex.Message}",
                           ModManager.LogLevel.Warn);
            return true;
        }
    }

    /// <summary>
    /// Sets the ambient copy for the duration of any add into one of our landblocks.
    ///
    /// This is where the cross-copy visibility bug lived. Adding an object leads to
    /// PhysicsObj.enter_world, which resolves the object's cell via
    /// LScape.get_landcell -> get_landblock. With no ambient set that returns ACE's
    /// ORIGINAL landblock, so the object is filed into the original's LandCells - and
    /// two players in different copies end up standing in the same EnvCell, seeing
    /// each other. The cells are per-landblock and genuinely private; only the lookup
    /// leaked.
    ///
    /// Patching the add itself rather than its callers matters for one specific reason:
    /// Landblock.Init() spawns a copy's creatures on a background Task.Run, and ambient
    /// is thread-local. Wrapping call sites would have left every monster in a copy
    /// filed into the original's cells.
    ///
    /// The previous value is saved and restored rather than cleared, since an add can
    /// happen while a copy is already being ticked.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObject))]
    public static void PreAddWorldObject(Landblock __instance, out Landblock? __state)
    {
        __state = Context.Ambient;

        if (InstanceWorld.IsOurs(__instance))
            Context.Ambient = __instance;
    }

    /// <summary>
    /// Restores the ambient copy, and scales anything that just entered one.
    ///
    /// This is the right seam for scaling because it is not only the initial spawn:
    /// generator respawns during a run reach a landblock through
    /// LandblockManager.AddObject, which calls straight into here, so a dungeon that
    /// repops mid-clear repops at the same difficulty it opened with.
    ///
    /// The ambient restore happens first and unconditionally. Scaling is downstream of
    /// it and wrapped, because this is as hot a path as this mod touches and a throw
    /// here would read as "the dungeon is broken" rather than as a scaling bug.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObject))]
    public static void PostAddWorldObject(Landblock __instance, WorldObject wo, bool __result, Landblock? __state)
    {
        Context.Ambient = __state;

        if (!__result)
            return;

        try
        {
            Scaling.Apply(__instance, wo);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not scale {wo?.Name ?? "?"}: {ex}", ModManager.LogLevel.Error);
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObjectForPhysics))]
    public static void PreAddForPhysics(Landblock __instance, out Landblock? __state)
    {
        __state = Context.Ambient;

        if (InstanceWorld.IsOurs(__instance))
            Context.Ambient = __instance;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Landblock), nameof(Landblock.AddWorldObjectForPhysics))]
    public static void PostAddForPhysics(Landblock? __state) => Context.Ambient = __state;


    /// <summary>
    /// True while this player is standing in one of our copies.
    ///
    /// Checked two ways because death is a busy moment: the landblock reference is the
    /// direct answer, and the assignment covers the case where the player has already
    /// been detached from it by the time the corpse is being built.
    /// </summary>
    private static bool InCopy(Player player)
    {
        if (InstanceWorld.IsOurs(player.CurrentLandblock))
            return true;

        if (player.Location is null)
            return false;

        var landblock = (ushort)(player.Location.LandblockId.Raw >> 16);

        return Context.CopyFor(player, landblock) > 0;
    }

    /// <summary>
    /// No item loss inside a copy. See Settings.NoItemLossInCopies for why this is a
    /// safety rail: the copy is destroyed when it empties, and a corpse full of your
    /// gear would go with it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.GetNumItemsDropped))]
    public static void PostGetNumItemsDropped(Player __instance, ref int __result)
    {
        try
        {
            if (__result != 0 && Mod.Settings.Enabled && Mod.Settings.NoItemLossInCopies && InCopy(__instance))
            {
                __result = 0;
                __instance.SendMessage("Nothing is lost from your corpse in here.");
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] item-loss check failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>Coins are counted separately from items, so they need the same rail.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.GetNumCoinsDropped))]
    public static void PostGetNumCoinsDropped(Player __instance, ref int __result)
    {
        try
        {
            if (__result != 0 && Mod.Settings.Enabled && Mod.Settings.NoItemLossInCopies && InCopy(__instance))
                __result = 0;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] coin-loss check failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    // ------------------------------------------------------------ fellowship sharing

    /// <summary>
    /// Fellowship experience does not cross copies.
    ///
    /// Fellowship.GetDistanceScalar decides two fellows are together by comparing
    /// landblock IDS and a 2D distance. Two copies of a dungeon share both, so a
    /// fellowship spread across personal copies looked, to that rule, like one group
    /// in one room: every kill in every copy was shared to all of them - N dungeons'
    /// worth of monsters feeding one share bonus. The landblock OBJECTS differ, and
    /// that is the test used here.
    ///
    /// Quest experience is left alone: ACE shares it at any distance on purpose, and a
    /// fellow in another copy is no further away than one in another town.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ACE.Server.Entity.Fellowship), nameof(ACE.Server.Entity.Fellowship.GetDistanceScalar))]
    public static void PostGetDistanceScalar(Player earner, Player fellow, XpType xpType, ref double __result)
    {
        try
        {
            if (__result > 0 && xpType != XpType.Quest && Mod.Settings.Enabled && InDifferentCopies(earner, fellow))
                __result = 0;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] fellowship xp check failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// The same rule for luminance and kill-task sharing. WithinRange already compares
    /// landblock objects, but when fellow_kt_landblock is on it ORs that with a plain
    /// distance, which two copies pass. Off today; this keeps it correct either way.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(ACE.Server.Entity.Fellowship), nameof(ACE.Server.Entity.Fellowship.WithinRange))]
    public static void PostWithinRange(Player player, List<Player> __result)
    {
        try
        {
            if (__result is { Count: > 0 } && Mod.Settings.Enabled)
                __result.RemoveAll(f => !ReferenceEquals(f, player) && InDifferentCopies(player, f));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] fellowship range check failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Two players standing in different copies of the SAME landblock. Different
    /// landblock ids are not our business - ACE's own distance rules handle those.
    /// </summary>
    private static bool InDifferentCopies(Player? a, Player? b)
    {
        var la = a?.CurrentLandblock;
        var lb = b?.CurrentLandblock;

        if (la is null || lb is null || ReferenceEquals(la, lb))
            return false;

        return la.Id.Landblock == lb.Id.Landblock;
    }


    /// <summary>
    /// Stops a copy loading the real dungeon's shard-persisted objects.
    ///
    /// Landblock.Init calls SpawnDynamicShardObjects, which fetches by landblock id:
    ///
    ///     GetDynamicObjectsByLandblock(Id.Landblock)
    ///
    /// Since a copy shares the source id, every copy would instantiate the SAME biota
    /// rows as the real dungeon - duplicate objects sharing guids across landblocks.
    /// Worse, WorldObject.Destroy calls RemoveBiotaFromDatabase unconditionally, so
    /// closing a copy would delete the real dungeon's persisted objects outright.
    ///
    /// A copy is meant to be a fresh instance of a dungeon's static content, never a
    /// second instantiation of persisted state, so it simply does not load any.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Landblock), "SpawnDynamicShardObjects")]
    public static bool PreSpawnDynamicShardObjects(Landblock __instance)
    {
        try
        {
            return !InstanceWorld.IsOurs(__instance);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] shard-object guard failed: {ex}", ModManager.LogLevel.Error);
            return true;
        }
    }

    /// <summary>
    /// Anything asking LandblockManager for a landblock while a copy is being ticked
    /// should get that copy. Guarded on the ambient tick only - a lookup with no
    /// instance context in play must never be diverted, or housing, allegiances and
    /// lifestones would start resolving into instances.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LandblockManager), nameof(LandblockManager.GetLandblock),
        new[] { typeof(LandblockId), typeof(bool), typeof(bool) })]
    public static bool PreGetLandblock(LandblockId landblockId, ref Landblock __result)
    {
        try
        {
            var ambient = Context.Ambient;

            if (ambient is null)
                return true;

            if (ambient.Id.LandblockX != landblockId.LandblockX ||
                ambient.Id.LandblockY != landblockId.LandblockY)
                return true;   // asking about a different landblock than the one ticking

            __result = ambient;
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] GetLandblock routing failed, falling back to stock: {ex}",
                           ModManager.LogLevel.Error);
            return true;
        }
    }

    /// <summary>
    /// Physics resolving a cell id to a landblock. LScape.get_landblock is static and
    /// takes only a cell id, so the ambient tick is the only context available - which
    /// is precisely why Context keeps one.
    ///
    /// Returning the copy's own PhysicsLandblock is what keeps collision inside the
    /// instance: each Landblock built its own, so two copies never share cells.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LScape), nameof(LScape.get_landblock))]
    public static bool PreGetPhysicsLandblock(uint blockCellID, ref ACE.Server.Physics.Common.Landblock __result)
    {
        try
        {
            var ambient = Context.Ambient;

            if (ambient is null)
                return true;

            var wanted = (ushort)(blockCellID >> 16);

            if (ambient.Id.Landblock != wanted)
                return true;

            __result = ambient.PhysicsLandblock;
            return false;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] physics landblock routing failed, falling back to stock: {ex}",
                           ModManager.LogLevel.Error);
            return true;
        }
    }
}
