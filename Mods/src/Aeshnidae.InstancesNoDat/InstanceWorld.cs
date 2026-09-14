namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Owns the extra copies of a landblock, and drives them.
///
/// Why this can work at all. A copy keeps the SOURCE landblock id, so the client
/// renders it out of the dat it already has - there is nothing to ship, no version
/// to bump, no DDD. That is the entire point of this mod, and the reason it cannot
/// repeat what the dat-patching approach cost us.
///
/// Three facts in ACE make it possible from a mod, without touching its source:
///
///   1. Landblock's constructor builds its OWN physics landblock -
///      `PhysicsLandblock = new Physics.Common.Landblock(cellLandblock)` - rather
///      than fetching a shared one out of LScape. So a second Landblock on the same
///      id gets private cells, and objects in one copy cannot collide with objects
///      in another. Isolation falls out of the constructor for free.
///
///   2. Landblock's constructor, Init() and all three Tick methods are public, so a
///      mod can create, populate and drive a landblock ACE knows nothing about.
///
///   3. WorldObject.CurrentLandblock is assigned in exactly three places, all inside
///      Landblock itself, and nothing re-derives it from position during normal
///      play. Once an object is inside a copy, almost every operation on it is
///      already correct without being told about instances.
///
/// ACE's own landblocks[,] array is never touched. The base landblock keeps loading
/// and ticking exactly as before for anyone not in an instance.
/// </summary>
public static class InstanceWorld
{
    /// <summary>
    /// Landblock keeps its player list private and offers no accessor, so this is the
    /// one place that reaches for it. Used only for reporting and for clearing people
    /// out of a copy that is being closed.
    /// </summary>
    private static readonly AccessTools.FieldRef<Landblock, List<Player>> _playersOf =
        AccessTools.FieldRefAccess<Landblock, List<Player>>("players");

    public static IReadOnlyList<Player> PlayersIn(Landblock? block)
    {
        if (block is null)
            return Array.Empty<Player>();

        try
        {
            lock (_playersOf(block))
                return _playersOf(block).ToList();
        }
        catch
        {
            return Array.Empty<Player>();
        }
    }

    /// <summary>(landblock, copy) -> the Landblock we created for it. Copy 0 is ACE's own.</summary>
    private static readonly ConcurrentDictionary<(ushort Landblock, int Copy), Landblock> _copies = new();

    public static int Count => _copies.Count;

    /// <summary>
    /// The real landblock for an id - ACE's own, never a copy, whatever is ambient.
    /// </summary>
    public static Landblock? Master(ushort landblock)
    {
        var previous = Context.Ambient;
        Context.Ambient = null;

        try
        {
            return LandblockManager.GetLandblock(new LandblockId((uint)landblock << 16 | 0xFFFF), false);
        }
        catch
        {
            return null;
        }
        finally
        {
            Context.Ambient = previous;
        }
    }

    /// <summary>
    /// Puts a player in a specific copy - or the master, for copy 0 - by moving them
    /// rather than by asking a teleport to do it.
    ///
    /// Every command that changes which copy you are in used to set an assignment and
    /// then teleport to the player's own position, trusting ACE to notice the landblock
    /// had changed. It often does not: two copies share a landblock id, so a teleport
    /// between them looks to ACE like a teleport to where you already are, and the
    /// transition that would have re-filed your physics never happens. The assignment
    /// then says one thing and the physics cells another, which is invisible to every
    /// check and obvious on screen - an empty room, or a room full of the wrong people.
    ///
    /// So the move is done here, explicitly, the same way a landblock crossing does it:
    /// remove with adjacencyMove false so the physics object is actually destroyed, then
    /// a full add so it is re-filed in the destination's cells. The teleport that follows
    /// is only for the client, to make it drop the old scene and draw the new one.
    /// </summary>
    public static bool PlaceIn(Player player, ushort landblock, int copy, string why)
    {
        // Same reason as the other opener in Patches.EnsureCopy: GetOrCreate may start
        // spawning on a background task, so the owner has to be on record before it is
        // called rather than after.
        Scaling.Remember(landblock, copy, player);

        var target = copy > 0 ? GetOrCreate(landblock, copy) : Master(landblock);

        if (target is null)
            return false;

        Context.Assign(player, landblock, copy, Mod.ScopeFor(landblock));
        Context.Force(player, landblock, copy);

        var from = player.CurrentLandblock;

        // Two things can be wrong independently, and only one of them was being checked.
        // Being LISTED in the right landblock and being physically in its cells are
        // separate facts: a player listed in the master with their physics still in the
        // copy they left satisfies ReferenceEquals(from, target) perfectly, so the move
        // was skipped and the half that was actually broken stayed broken. The audit
        // caught both of us in that state at once, inverted.
        var listedRight = ReferenceEquals(from, target);
        var physicallyRight = IsPhysicallyIn(player, target);

        if (listedRight && physicallyRight)
        {
            // Already exactly where they should be. Returning here rather than falling
            // through to the teleport is the difference between a quiet no-op and a
            // player being visibly yanked through a portal for no reason - and this is
            // reached on every mod reload and every audit pass, so an unconditional
            // teleport is felt as the dungeon randomly grabbing you.
            Context.Assign(player, landblock, copy, Mod.ScopeFor(landblock));
            return true;
        }

        {
            var previous = Context.Ambient;
            Context.Ambient = target;

            try
            {
                from?.RemoveWorldObjectForPhysics(player.Guid, adjacencyMove: false);

                // Removing and re-adding within ONE landblock needs the removal applied
                // first: both queues are keyed by guid, and pending additions are
                // processed before pending removals - so without this the re-add is
                // undone by the removal it was meant to follow.
                if (listedRight)
                    Drain(target);

                target.AddWorldObject(player);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] could not move {player.Name} into " +
                               $"{(copy > 0 ? $"copy {copy}" : "the master")} of {landblock:X4}: {ex}",
                               ModManager.LogLevel.Error);
                return false;
            }
            finally
            {
                Context.Ambient = previous;
            }
        }

        WorldManager.ThreadSafeTeleport(player, new Position(player.Location));

        // Check its own work. The remove-then-add above is only as good as the removal:
        // Landblock.RemoveWorldObjectInternal returns early without touching physics if
        // the object is not in that landblock's worldObjects, and on the reload path the
        // block being left has already been emptied - so CurCell survives, the add sees a
        // non-null cell, skips AddPhysicsObj entirely, and the player is listed in one
        // landblock while standing in another's cells.
        //
        // Destroying the physics object directly leaves nothing for the add to skip.
        if (!IsPhysicallyIn(player, target))
        {
            var previous = Context.Ambient;
            Context.Ambient = target;

            try
            {
                player.PhysicsObj?.DestroyObject();
                target.AddWorldObject(player);

                var settled = IsPhysicallyIn(player, target);

                ModManager.Log($"[{Mod.Name}] {player.Name} needed a forced physics re-entry into " +
                               $"{(copy > 0 ? $"copy {copy}" : "the master")} of {landblock:X4} " +
                               $"({(settled ? "now correct" : "not yet - will retry")})",
                               ModManager.LogLevel.Warn);

                if (!settled)
                    _retry[player.Guid.Full] = (landblock, copy, 1);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] forced physics re-entry for {player.Name} failed: {ex.Message}",
                               ModManager.LogLevel.Error);
            }
            finally
            {
                Context.Ambient = previous;
            }
        }

        // Whoever was in the landblock they left has a client that may still be drawing
        // them, and the message that would have said otherwise was queued on a landblock
        // that might never tick again.
        ResyncVisibility(landblock);

        Mod.Trace($"{player.Name} placed in {(copy > 0 ? $"copy {copy}" : "the master")} " +
                  $"of {landblock:X4} ({why}), moved from copy {CopyOf(from)}");

        return true;
    }

    /// <summary>
    /// Where a matching object actually IS, in physics terms, rather than which
    /// dictionary lists it.
    ///
    /// Every check this mod had answered the second question, and they kept agreeing
    /// with each other while disagreeing with the screen - because being in a landblock's
    /// worldObjects and being in that landblock's physics cells are separate facts, and
    /// only the second one decides what a player can see.
    /// </summary>
    public static List<string> DescribeMatching(Landblock? block, string text)
    {
        var found = new List<string>();

        if (block is null || string.IsNullOrWhiteSpace(text))
            return found;

        foreach (var wo in _objectsOf(block).Values)
        {
            if (wo.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) != true)
                continue;

            found.Add($"{wo.Name} {Describe(wo)}");
        }

        return found;
    }

    /// <summary>
    /// Which landblock's cell space an object is physically in, by reference.
    ///
    /// The obvious check - ObjCell.CurLandblock - is a trap indoors: it is assigned in
    /// exactly one place in ACE, on outdoor LandCells, so for every dungeon cell it is
    /// null and any comparison against it fails for everything equally. A diagnostic that
    /// says the same thing about every object is worse than none, because it looks like
    /// evidence.
    ///
    /// Each Physics.Common.Landblock keeps its own LandCells dictionary, and that really
    /// is what separates one copy from another - LScape.get_landcell reads indoor cells
    /// straight out of it. So the honest question is which landblock's dictionary holds
    /// THIS cell object, and reference equality answers it exactly.
    /// </summary>
    private static string OwnerOfCell(ACE.Server.Physics.Common.ObjCell cell)
    {
        var landblock = (ushort)(cell.ID >> 16);
        var key = (int)(cell.ID & 0xFFFF);

        try
        {
            var master = Master(landblock);

            if (master?.PhysicsLandblock?.LandCells.TryGetValue(key, out var inMaster) == true
                && ReferenceEquals(inMaster, cell))
                return "the master";

            foreach (var (id, block) in _copies)
            {
                if (id.Landblock != landblock)
                    continue;

                if (block.PhysicsLandblock?.LandCells.TryGetValue(key, out var inCopy) == true
                    && ReferenceEquals(inCopy, cell))
                    return $"copy {id.Copy}";
            }
        }
        catch { /* a diagnostic must never be the thing that breaks */ }

        return "a landblock nobody owns any more - a closed copy";
    }

    /// <summary>
    /// Checks that players only know about each other when they are actually together.
    ///
    /// Placement being right is not the same as visibility being right. Two players can
    /// each sit correctly in their own copy while one of their clients still holds the
    /// other from an earlier moment - the "remove this object" message is queued on the
    /// landblock being LEFT, so if that landblock stops being ticked before the queue
    /// drains, the message is never sent and the client believes in someone who is no
    /// longer there. It shows up as one-way visibility, which no check of cells or
    /// listings can see, because both are correct.
    ///
    /// Repaired directly rather than by moving anyone: the placement is not what is
    /// wrong, so the tracking is what gets corrected.
    /// </summary>
    private static void AuditVisibility()
    {
        var players = PlayerManager.GetAllOnline()
            .Where(p => p?.Location is not null
                        && Mod.IsInstanced((ushort)(p.Location.LandblockId.Raw >> 16)))
            .ToList();

        foreach (var player in players)
        {
            var known = player.GetKnownObjects();

            foreach (var other in players)
            {
                if (ReferenceEquals(player, other))
                    continue;

                var together = ReferenceEquals(player.CurrentLandblock, other.CurrentLandblock);
                var knowsThem = known.Any(k => k.Guid == other.Guid);

                if (together == knowsThem)
                    continue;

                if (knowsThem)
                {
                    ModManager.Log($"[{Mod.Name}] AUDIT {player.Name} can see {other.Name}, who is " +
                                   $"in a different copy - dropping the stale tracking",
                                   ModManager.LogLevel.Warn);

                    try { player.RemoveTrackedObject(other, false); } catch { }
                }
                else
                {
                    ModManager.Log($"[{Mod.Name}] AUDIT {player.Name} cannot see {other.Name}, who is " +
                                   $"in the same copy - adding the missing tracking",
                                   ModManager.LogLevel.Warn);

                    try { player.AddTrackedObject(other); } catch { }
                }
            }
        }
    }

    /// <summary>
    /// Tells every client in a landblock the truth about every other player in it.
    ///
    /// The failure this exists for is invisible from the server: a client that was never
    /// told to drop somebody keeps drawing them, while the server's own tracking is
    /// perfectly clean. Nothing server-side disagrees, so nothing can detect it - the
    /// only remedy is to send the message that went missing.
    ///
    /// It goes missing because "forget this object" is queued on the landblock being
    /// LEFT. A copy that closes, or simply stops being ticked, never drains that queue.
    /// So these are sent straight to the sessions instead, where no landblock's tick
    /// stands between the decision and the delivery.
    ///
    /// Sending a delete for something a client does not have is harmless, which is what
    /// makes this safe to run whenever a player moves rather than only when a problem
    /// has been proven.
    /// </summary>
    public static void ResyncVisibility(ushort landblock)
    {
        try
        {
            var players = PlayerManager.GetAllOnline()
                .Where(p => p?.Location is not null
                            && (ushort)(p.Location.LandblockId.Raw >> 16) == landblock)
                .ToList();

            foreach (var player in players)
            {
                foreach (var other in players)
                {
                    if (ReferenceEquals(player, other))
                        continue;

                    if (ReferenceEquals(player.CurrentLandblock, other.CurrentLandblock))
                    {
                        try { player.AddTrackedObject(other); } catch { }
                        continue;
                    }

                    try
                    {
                        player.Session?.Network.EnqueueSend(new GameMessageDeleteObject(other));
                    }
                    catch { /* a courtesy message must not break a move */ }
                }
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] visibility resync failed: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    /// <summary>Is this object's physics cell owned by this landblock?</summary>
    public static bool IsPhysicallyIn(WorldObject wo, Landblock block)
    {
        var cell = wo.PhysicsObj?.CurCell;

        if (cell is null || block.PhysicsLandblock is null)
            return false;

        return block.PhysicsLandblock.LandCells.TryGetValue((int)(cell.ID & 0xFFFF), out var owned)
               && ReferenceEquals(owned, cell);
    }

    /// <summary>Where an object physically is, against where it is listed.</summary>
    public static string Describe(WorldObject wo)
    {
        var cell = wo.PhysicsObj?.CurCell;

        if (cell is null)
            return "has NO physics cell - listed, but not in the world";

        var listed = wo.CurrentLandblock;
        var listedAs = IsOrphaned(listed) ? "a closed copy"
                     : CopyOf(listed) > 0 ? $"copy {CopyOf(listed)}"
                     : "the master";

        var physically = OwnerOfCell(cell);
        var agree = physically == listedAs;

        return $"cell 0x{cell.ID:X8} - physically in {physically}, listed in {listedAs}" +
               (agree ? " (agree)" : "  <-- MISMATCH");
    }

    /// <summary>How many objects a copy holds, players included.</summary>
    public static int ObjectCount(Landblock? block) => block is null ? 0 : _objectsOf(block).Count;

    /// <summary>
    /// Objects in a copy whose name contains this text. Exists so "how many drudges are
    /// actually in copy 2" is a question with an answer, rather than something inferred
    /// from teardown counts.
    /// </summary>
    public static int CountMatching(Landblock? block, string text)
    {
        if (block is null || string.IsNullOrWhiteSpace(text))
            return 0;

        return _objectsOf(block).Values
            .Count(wo => wo.Name?.Contains(text, StringComparison.OrdinalIgnoreCase) == true);
    }

    public static int OpenCount(ushort landblock) => _copies.Keys.Count(k => k.Landblock == landblock);

    public static IEnumerable<((ushort Landblock, int Copy) Key, Landblock Block)> All =>
        _copies.Select(kv => (kv.Key, kv.Value));

    /// <summary>
    /// The copy for a landblock, creating and populating it on first use.
    ///
    /// Copy 0 is deliberately not ours - it is ACE's own landblock, reached through
    /// LandblockManager as usual, so a player who is not in an instance is on
    /// completely stock behaviour.
    /// </summary>
    public static Landblock? GetOrCreate(ushort landblock, int copy)
    {
        if (copy <= 0)
            return null;

        return _copies.GetOrAdd((landblock, copy), key =>
        {
            var id = new LandblockId((uint)key.Landblock << 16 | 0xFFFF);
            var block = new Landblock(id);

            // Populates the copy with its own creatures, generators and encounters.
            // Init spawns on a background task, exactly as it does for ACE's own
            // landblocks, so the copy is live a moment after this returns.
            block.Init();

            ModManager.Log($"[{Mod.Name}] created copy {key.Copy} of {key.Landblock:X4} " +
                           $"with its own physics cells and spawns");

            return block;
        });
    }

    public static Landblock? Find(ushort landblock, int copy) =>
        copy > 0 && _copies.TryGetValue((landblock, copy), out var block) ? block : null;

    /// <summary>True if this Landblock is one of ours rather than ACE's.</summary>
    public static bool IsOurs(Landblock? block) =>
        block is not null && _copies.Values.Contains(block);

    /// <summary>
    /// A landblock nobody owns any more: not a live copy, and not the server's landblock
    /// for its own id either. A player holding one is in an emptied room that is never
    /// ticked, and every check that asks "which copy is this" answers 0 - so the state
    /// reads as "the master" while being nothing of the kind. Worth naming, because it
    /// is the one condition that looks identical to working from every angle except the
    /// player's screen.
    /// </summary>
    public static bool IsOrphaned(Landblock? block)
    {
        if (block is null || IsOurs(block))
            return false;

        try
        {
            return !ReferenceEquals(LandblockManager.GetLandblock(block.Id, false), block);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The copy number of one of our landblocks, or 0 if it is ACE's.</summary>
    public static int CopyOf(Landblock? block)
    {
        if (block is null)
            return 0;

        foreach (var (key, value) in _copies)
        {
            if (ReferenceEquals(value, block))
                return key.Copy;
        }

        return 0;
    }

    /// <summary>
    /// Drives our copies, mirroring the order LandblockManager.Tick uses: physics
    /// first, collecting anything that moved, then the multi-threaded work, then the
    /// single-threaded work.
    ///
    /// Deliberately single-threaded regardless of the server's landblock threading
    /// settings. ACE parallelises by landblock GROUP, which exists to keep landblocks
    /// that can see each other on one thread; our copies are self-contained interiors
    /// with no adjacency, so there is no group to reason about and no benefit worth
    /// the risk of getting the isolation wrong.
    ///
    /// Ambient is set around each copy's tick so that anything reaching for "the
    /// landblock at this cell" during physics resolves to the copy being ticked
    /// rather than to ACE's original. See Context.
    /// </summary>
    public static void Tick(double portalYearTicks)
    {
        if (!_healed)
        {
            _healed = true;
            HealAfterLoad();
        }

        RetryPlacements();
        Audit();

        if (_copies.IsEmpty)
            return;

        var moved = new ConcurrentBag<WorldObject>();

        foreach (var block in _copies.Values)
        {
            Context.Ambient = block;

            try
            {
                block.TickPhysics(portalYearTicks, moved);
            }
            finally
            {
                Context.Ambient = null;
            }
        }

        // A move that leaves the copy entirely is handled by ACE's own relocation
        // path, which our patch teaches to keep an object in the copy it is already
        // in when the landblock id has not changed.
        foreach (var wo in moved)
        {
            try
            {
                LandblockManager.RelocateObjectForPhysics(wo, true);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] relocation failed for {wo.Name}: {ex.Message}",
                               ModManager.LogLevel.Error);
            }
        }

        var now = Time.GetUnixTime();

        foreach (var block in _copies.Values)
        {
            Context.Ambient = block;

            try
            {
                block.TickMultiThreadedWork(now);
                block.TickSingleThreadedWork(now);
            }
            finally
            {
                Context.Ambient = null;
            }
        }

        if (Mod.Settings.CloseWhenEmpty)
            CloseEmptyCopies(TimeSpan.FromSeconds(Mod.Settings.EmptyGraceSeconds));
    }

    private static readonly AccessTools.FieldRef<Landblock, Dictionary<ObjectGuid, WorldObject>> _objectsOf =
        AccessTools.FieldRefAccess<Landblock, Dictionary<ObjectGuid, WorldObject>>("worldObjects");

    /// <summary>When a copy became empty, for the grace period before it is closed.</summary>
    private static readonly ConcurrentDictionary<(ushort, int), DateTime> _emptySince = new();

    /// <summary>
    /// Tears a copy down WITHOUT going through Landblock.Unload, which is unsafe for
    /// our copies on two counts:
    ///
    ///   SaveDB() persists anything dynamic - corpses, dropped loot - as belonging to
    ///   the landblock id. Since a copy shares the source id, an instance's leftovers
    ///   would reappear in the real dungeon the next time it loaded.
    ///
    ///   LScape.unload_landblock(id) is keyed on the landblock id alone, so unloading
    ///   one copy would rip out an entry shared with the real landblock and every
    ///   sibling copy.
    ///
    /// So instance contents are destroyed rather than saved, and LScape is left alone -
    /// our physics landblock was built directly by the constructor and never registered
    /// there in the first place.
    /// </summary>
    /// <summary>
    /// Empties a copy of everything that is not a player.
    ///
    /// Deliberately NOT Landblock.DestroyAllNonPlayerObjects, which opens with SaveDB()
    /// - and SaveDB on a copy writes that copy's corpses and dropped loot to the shard
    /// database against the REAL landblock id, so an instance's leavings would surface
    /// in the public dungeon. Same reason Close() avoids Unload().
    ///
    /// Destroy() deletes the biota from the shard database unconditionally, so anything
    /// database-backed is only detached. This is the guard Landblock.Unload itself uses,
    /// and without it emptying a copy would delete rows belonging to the real dungeon.
    /// Copies should not hold such objects now that SpawnDynamicShardObjects is skipped
    /// for them, but this is what makes that safe rather than merely likely.
    /// </summary>
    private static void DestroyContents(Landblock block)
    {
        foreach (var wo in _objectsOf(block).Values.ToList())
        {
            if (wo is Player)
                continue;

            try
            {
                if (wo.BiotaOriginatedFromOrHasBeenSavedToDatabase())
                    block.RemoveWorldObject(wo.Guid);
                else
                    wo.Destroy(false, true);
            }
            catch { /* one stubborn object must not strand the rest */ }
        }
    }

    private static DateTime _lastAudit = DateTime.MinValue;

    /// <summary>When a player was first seen in a state that should not exist.</summary>
    private static readonly ConcurrentDictionary<uint, DateTime> _mismatchSince = new();

    /// <summary>
    /// Watches for players whose physics cells and listed landblock disagree, and says so
    /// in the log.
    ///
    /// This state is the one failure this design produces that looks completely healthy
    /// from the server side: every dictionary agrees, every command reports success, and
    /// the only symptom is on somebody's screen. It has cost several rounds of chasing
    /// each time it has appeared, always diagnosed by asking a player what they could
    /// see. It is cheap to check directly - a handful of players against a couple of
    /// landblocks - so there is no reason to keep learning about it second hand.
    /// </summary>
    private static void Audit()
    {
        if (DateTime.UtcNow - _lastAudit < TimeSpan.FromSeconds(15))
            return;

        _lastAudit = DateTime.UtcNow;

        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.LogRouting)
                return;

            foreach (var player in PlayerManager.GetAllOnline())
            {
                if (player?.Location is null)
                    continue;

                var landblock = (ushort)(player.Location.LandblockId.Raw >> 16);

                if (!Mod.IsInstanced(landblock))
                    continue;

                var described = Describe(player);

                if (!described.Contains("MISMATCH") && !described.Contains("NO physics cell"))
                {
                    _mismatchSince.TryRemove(player.Guid.Full, out _);
                    continue;
                }

                ModManager.Log($"[{Mod.Name}] AUDIT {player.Name}: {described}",
                               ModManager.LogLevel.Warn);

                // Repaired rather than merely reported, but only once it has persisted.
                // A player halfway through a legitimate move mismatches for a moment by
                // definition, and repairing that would be a fight rather than a fix.
                var since = _mismatchSince.GetOrAdd(player.Guid.Full, DateTime.UtcNow);

                if (DateTime.UtcNow - since < TimeSpan.FromSeconds(20))
                    continue;

                _mismatchSince.TryRemove(player.Guid.Full, out _);

                // Where they SHOULD be: their assignment, or the master for staff, who
                // are never given copies automatically.
                var belongs = Mod.IsStaff(player) ? 0 : Context.CopyFor(player, landblock);

                ModManager.Log($"[{Mod.Name}] AUDIT repairing {player.Name} into " +
                               $"{(belongs > 0 ? $"copy {belongs}" : "the master")} of {landblock:X4}",
                               ModManager.LogLevel.Warn);

                PlaceIn(player, landblock, belongs, "audit repair");
            }

            AuditVisibility();

            foreach (var (id, block) in _copies.ToList())
            {
                foreach (var wo in _objectsOf(block).Values.ToList())
                {
                    if (wo is Player)
                        continue;

                    var described = Describe(wo);

                    if (described.Contains("MISMATCH"))
                    {
                        ModManager.Log($"[{Mod.Name}] AUDIT {wo.Name} in copy {id.Copy}: {described}",
                                       ModManager.LogLevel.Warn);
                        break;   // one example per copy is enough to identify the fault
                    }
                }
            }

            var master = Master(_copies.Keys.Select(k => k.Landblock).FirstOrDefault());

            if (master is not null)
            {
                foreach (var wo in _objectsOf(master).Values.ToList())
                {
                    if (wo is Player)
                        continue;

                    var described = Describe(wo);

                    if (described.Contains("MISMATCH"))
                    {
                        ModManager.Log($"[{Mod.Name}] AUDIT {wo.Name} in the master: {described}",
                                       ModManager.LogLevel.Warn);
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] audit failed: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// Players whose placement did not take, to be tried again on later ticks.
    ///
    /// A copy created a moment ago is not yet a place you can stand in: Landblock.Init
    /// populates its cells and spawns its contents on a background task, so a physics
    /// entry attempted in the same breath as the creation can simply fail. It then took
    /// the audit half a minute to notice and redo it - correct, but a long time to be
    /// standing in the wrong dungeon.
    ///
    /// Retrying on the following ticks closes that window to milliseconds, and the
    /// attempt count is logged so a placement that never succeeds is visible rather
    /// than merely slow.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, (ushort Landblock, int Copy, int Attempts)> _retry = new();

    private const int MaxPlacementAttempts = 20;

    private static void RetryPlacements()
    {
        if (_retry.IsEmpty)
            return;

        foreach (var (guid, pending) in _retry.ToList())
        {
            var player = PlayerManager.GetOnlinePlayer(guid);

            if (player is null)
            {
                _retry.TryRemove(guid, out _);
                continue;
            }

            var target = pending.Copy > 0 ? Find(pending.Landblock, pending.Copy) : Master(pending.Landblock);

            if (target is null)
            {
                _retry.TryRemove(guid, out _);
                continue;
            }

            if (IsPhysicallyIn(player, target))
            {
                _retry.TryRemove(guid, out _);

                Mod.Trace($"{player.Name} settled into " +
                          $"{(pending.Copy > 0 ? $"copy {pending.Copy}" : "the master")} of " +
                          $"{pending.Landblock:X4} after {pending.Attempts} attempt(s)");
                continue;
            }

            if (pending.Attempts >= MaxPlacementAttempts)
            {
                _retry.TryRemove(guid, out _);

                ModManager.Log($"[{Mod.Name}] gave up placing {player.Name} in " +
                               $"{(pending.Copy > 0 ? $"copy {pending.Copy}" : "the master")} of " +
                               $"{pending.Landblock:X4} after {pending.Attempts} attempts - the audit " +
                               "will keep trying", ModManager.LogLevel.Error);
                continue;
            }

            _retry[guid] = (pending.Landblock, pending.Copy, pending.Attempts + 1);

            var previous = Context.Ambient;
            Context.Ambient = target;

            try
            {
                player.PhysicsObj?.DestroyObject();
                target.AddWorldObject(player);
            }
            catch { /* the next tick will try again */ }
            finally
            {
                Context.Ambient = previous;
            }
        }
    }

    private static bool _healed;

    /// <summary>
    /// Puts players who are standing in an instanced dungeon back into a copy, once, on
    /// the first tick after this mod loads.
    ///
    /// Copies and assignments live only in this assembly's memory, so a hot reload
    /// starts from nothing while the players carry on standing where they were. Every
    /// one of them is then in the shared landblock - the version these dungeons are not
    /// supposed to have - and nothing rescued them until they happened to cross a
    /// landblock boundary. That is a poor thing to rely on, and worse, it made every
    /// test after a rebuild start from a state nobody had asked for.
    ///
    /// Admins are left alone deliberately: an admin in the master is usually there on
    /// purpose, authoring, and yanking them into a copy would undo the one workflow
    /// /inst master exists to support.
    ///
    /// The teleport is to the player's own position - same geometry, so the client sees
    /// nothing unusual - and the forced destination is what carries them into the copy
    /// rather than back to the shared landblock.
    /// </summary>
    private static void HealAfterLoad()
    {
        try
        {
            if (!Mod.Settings.Enabled)
                return;

            foreach (var player in PlayerManager.GetAllOnline())
            {
                if (player?.Location is null)
                    continue;

                var landblock = (ushort)(player.Location.LandblockId.Raw >> 16);

                if (!Mod.IsInstanced(landblock))
                    continue;

                // Admins go to the master rather than a copy - an admin in an instanced
                // dungeon is usually authoring, and handing them a private copy would
                // undo the workflow /inst master exists for.
                //
                // But they still have to be MOVED. Skipping the teleport entirely left
                // an admin who happened to be in a copy holding the block that had just
                // been emptied and dropped: never ticked again, and reporting itself as
                // copy 0 because it is no longer in _copies, so every log line and every
                // /instance said "the master" while their screen showed an empty room.
                if (Mod.IsStaff(player))
                {
                    PlaceIn(player, landblock, 0, "reload, staff stay in the master");
                    continue;
                }

                // The group's copy first, and only then a new one. Without this a
                // fellowship of two standing in a dungeon when the mod reloads is split:
                // the first member is given copy 1 under the fellowship's key, and the
                // second is handed copy 2 anyway, because nothing asked where their
                // fellowship had already been put. Groups staying together is the whole
                // point of fellowship scope, and a rebuild would have broken it every
                // time.
                var copy = Context.CopyFor(player, landblock);

                if (copy == 0)
                    copy = AllocateCopy(landblock, Mod.Settings.MaxCopiesPerLandblock);

                if (copy == 0 || GetOrCreate(landblock, copy) is null)
                {
                    ModManager.Log($"[{Mod.Name}] could not give {player.Name} a copy of " +
                                   $"{landblock:X4} after reload", ModManager.LogLevel.Warn);
                    continue;
                }

                PlaceIn(player, landblock, copy, "reload");
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not restore players after reload: {ex}",
                           ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Runs a copy's own queues until the work waiting in them has actually happened.
    ///
    /// Both halves of a teardown are deferred by ACE: removals sit in pendingRemovals,
    /// and the messages telling clients to forget those objects sit in the landblock's
    /// action queue. Ticking the landblock is what applies them, so anything that stops
    /// ticking a copy before draining it leaves the client believing in objects the
    /// server has already thrown away.
    ///
    /// Two passes because the first pass's removals can enqueue the second pass's
    /// broadcasts.
    /// </summary>
    private static void Drain(Landblock block)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                block.TickSingleThreadedWork(Time.GetUnixTime());
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] draining a copy was untidy: {ex.Message}",
                               ModManager.LogLevel.Warn);
                return;
            }
        }
    }

    /// <summary>
    /// Moves anyone still inside a copy out of it, to their lifestone.
    ///
    /// A copy that is torn down under someone's feet is the worst state this mod can
    /// produce: they keep a reference to a Landblock that is no longer ticked, so its
    /// creatures freeze mid-animation and its deaths never reach the client. Ejecting
    /// first means the client is told to drop the whole scene, which is precisely the
    /// message that clears the ghosts.
    ///
    /// The lifestone rather than the shared dungeon, because an instanced dungeon is
    /// not meant to have a public version to be dropped into.
    /// </summary>
    public static int EjectPlayers(Landblock block, string why)
    {
        var moved = 0;

        foreach (var player in PlayersIn(block))
        {
            try
            {
                var destination = player.Sanctuary is not null
                    ? new Position(player.Sanctuary)
                    : new Position(player.Location);

                player.SendMessage(why);
                WorldManager.ThreadSafeTeleport(player, destination);
                moved++;
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] could not move {player.Name} out: {ex.Message}",
                               ModManager.LogLevel.Warn);
            }
        }

        return moved;
    }

    /// <summary>
    /// Re-reads the master version of a dungeon into every open copy of it.
    ///
    /// There is exactly one authored version of a landblock - the world database rows
    /// for that id - and every copy spawns from it. So the dev loop is: edit the master,
    /// then push it here. ACE's own /reload-landblock only ever touches the single
    /// landblock the caller is standing in, which for an instanced dungeon is one copy
    /// out of however many are open.
    ///
    /// The world database cache is cleared once rather than per copy, since all copies
    /// read the same rows under the same id.
    /// </summary>
    public static int ReloadAllCopies(ushort landblock)
    {
        DatabaseManager.World.ClearCachedInstancesByLandblock(landblock);

        var open = _copies.Keys.Count(k => k.Landblock == landblock);

        Mod.Trace($"reload of {landblock:X4}: world database cache cleared, {open} copy(ies) open");

        var reloaded = 0;

        foreach (var (key, block) in _copies.ToList())
        {
            if (key.Landblock != landblock)
                continue;

            var previous = Context.Ambient;
            Context.Ambient = block;

            try
            {
                var before = _objectsOf(block).Count;

                DestroyContents(block);

                // Before the respawn, not after. A landblock instance comes back with
                // the SAME guid it had before, and pending removals are keyed by guid -
                // so a removal still queued when the replacement is added takes the
                // replacement with it, and the reload silently achieves nothing.
                Drain(block);

                block.Init(reload: true);
                reloaded++;

                Mod.Trace($"reloaded copy {key.Copy} of {landblock:X4}: {before} object(s) cleared, " +
                          "respawning from the master's world database rows");
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] could not reload copy {key.Copy} of {landblock:X4}: {ex}",
                               ModManager.LogLevel.Error);
            }
            finally
            {
                Context.Ambient = previous;
            }
        }

        return reloaded;
    }

    public static bool Close(ushort landblock, int copy, bool ejectPlayers = true)
    {
        if (!_copies.TryRemove((landblock, copy), out var block))
            return false;

        _emptySince.TryRemove((landblock, copy), out _);

        // Without this, a group returning after their copy closed would be routed to a
        // copy number that no longer exists and fall through to the shared landblock.
        Context.ForgetCopy(landblock, copy);
        Scaling.Forget(landblock, copy);

        var dropped = Registry.Forget(landblock, copy);

        var previous = Context.Ambient;
        Context.Ambient = block;

        try
        {
            DestroyContents(block);

            // The drain, and the whole reason ghosts happened.
            //
            // Removing an object does not tell the client anything directly - it QUEUES
            // "forget this object" onto the landblock's own action queue
            // (Landblock.RemoveWorldObjectInternal -> EnqueueActionBroadcast). A copy
            // that has been taken out of _copies is never ticked again, so that queue is
            // never run, the messages are never sent, and every creature the player could
            // see stays on their screen forever, frozen mid-animation. It was never a
            // physics problem; the client was simply never told.
            //
            // This has to happen while the players are STILL STANDING HERE, because they
            // are the recipients of those messages. Hence destroy, drain, and only then
            // move anyone out.
            Drain(block);

            // Not on a reload. A reload is followed immediately by HealAfterLoad, which
            // puts these same players into a fresh copy on the next tick - so leaving
            // them standing where they are costs them a respawned dungeon, while
            // recalling them costs them the walk back. The draining above is what makes
            // staying put safe: they are briefly in the master with a clean scene rather
            // than in a landblock nobody ticks.
            if (ejectPlayers)
                EjectPlayers(block, "The dungeon around you has been closed. You have been recalled.");

            block.PhysicsLandblock.release_shadow_objs();

            ModManager.Log($"[{Mod.Name}] closed copy {copy} of {landblock:X4}, " +
                           $"contents destroyed rather than saved, {dropped} physics registration(s) dropped");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] closing copy {copy} of {landblock:X4} was untidy: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
        finally
        {
            Context.Ambient = previous;
        }

        return true;
    }

    /// <summary>
    /// The lowest free copy number for a landblock, so slots are reused as people come
    /// and go rather than climbing forever.
    /// </summary>
    public static int AllocateCopy(ushort landblock, int max)
    {
        for (var copy = 1; copy <= max; copy++)
        {
            if (!_copies.ContainsKey((landblock, copy)))
                return copy;
        }

        return 0;   // every slot is occupied
    }

    /// <summary>
    /// Closes copies nobody is standing in any more.
    ///
    /// Deliberately a sweep rather than a hook on the way out. Leaving an instance is
    /// not one event - it is a teleport, a recall, a death, a logout, a dropped
    /// connection - and hooking each of them means missing the one that gets added
    /// later. Emptiness is the condition that actually matters, so that is what is
    /// checked.
    ///
    /// The grace period keeps a copy alive across a momentary gap, so a player who
    /// dies and releases, or whose teleport takes a beat, does not come back to find
    /// their dungeon reset.
    /// </summary>
    public static void CloseEmptyCopies(TimeSpan grace)
    {
        foreach (var (key, block) in _copies.ToList())
        {
            if (PlayersIn(block).Count > 0)
            {
                _emptySince.TryRemove(key, out _);
                continue;
            }

            var since = _emptySince.GetOrAdd(key, DateTime.UtcNow);

            if (DateTime.UtcNow - since >= grace)
                Close(key.Landblock, key.Copy);
        }
    }

    /// <summary>
    /// Drops every copy. Players inside one are sent to their lifestone first, since
    /// their position is inside a landblock that is about to stop existing.
    /// </summary>
    /// <summary>
    /// Closes every copy, properly.
    ///
    /// This used to be a bare _copies.Clear(), which was the single worst bug in the
    /// mod: it dropped the dictionary while leaving the Landblock objects alive with
    /// players standing in them. Nothing ticked those landblocks afterwards, so their
    /// creatures froze mid-animation and deaths never reached the client - the "broken
    /// drudge that will not die". It logged nothing at all, so a copy vanishing this way
    /// was indistinguishable from a copy that was never created.
    ///
    /// Mod.Dispose calls this, which means every hot reload ran it. That is why a rebuild
    /// left testers in landblocks that no longer existed.
    /// </summary>
    public static void Clear(bool ejectPlayers = true)
    {
        var keys = _copies.Keys.ToList();

        foreach (var (landblock, copy) in keys)
            Close(landblock, copy, ejectPlayers);

        // Anything Close could not take (it removes from _copies itself) must not be
        // left behind holding players.
        _copies.Clear();
        Context.ForgetAll();
        Registry.ForgetAll();
        Scaling.ForgetAll();

        if (keys.Count > 0)
            ModManager.Log($"[{Mod.Name}] closed all {keys.Count} copy(ies)");
    }
}
