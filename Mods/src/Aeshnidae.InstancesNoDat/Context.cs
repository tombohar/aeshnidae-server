namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Answers the only question the whole design turns on: when something asks for
/// "the landblock at this position", which copy does it get?
///
/// A position cannot answer it. Two copies of a dungeon have identical landblock ids
/// and identical cell ids, so Position is not enough to tell them apart - that is the
/// price of keeping the source id, and it is a price worth paying because it is what
/// makes the client need nothing.
///
/// So the answer comes from two places instead, in this order:
///
///   1. AMBIENT - the copy currently being ticked. Physics reaches for landblocks
///      through static helpers that take a cell id and no requester, so there is
///      nothing to thread context through. A thread-local set around each copy's
///      tick covers it. This is not a hack grafted on: ACE already keeps
///      LandblockManager.CurrentMultiThreadedTickingLandblockGroup as a ThreadLocal
///      for the same class of problem, so it runs with the grain.
///
///   2. ASSIGNMENT - which copy a given player belongs in. Consulted when someone
///      enters fresh from outside, which is the one moment a copy has to be chosen
///      rather than inferred.
///
/// Everything else needs neither, because an object already inside a copy holds a
/// direct CurrentLandblock reference to it.
/// </summary>
public static class Context
{
    private static readonly ThreadLocal<Landblock?> _ambient = new();

    /// <summary>The copy being ticked on this thread, if any.</summary>
    public static Landblock? Ambient
    {
        get => _ambient.Value;
        set => _ambient.Value = value;
    }

    /// <summary>
    /// (group key, landblock) -> copy. The key is a player guid, a fellowship leader's
    /// guid or an allegiance monarch's guid depending on the dungeon's scope, so
    /// "who shares this" is a question of which key is used and not a separate
    /// mechanism per case.
    /// </summary>
    private static readonly ConcurrentDictionary<(uint Group, ushort Landblock), int> _assigned = new();

    public static int AssignedCount => _assigned.Count;

    /// <summary>
    /// The key that owns a copy for this player, given the dungeon's scope.
    ///
    /// Falls back to the player's own guid when they have no group - a soloist in a
    /// fellowship dungeon gets their own copy rather than being refused, and someone
    /// unsworn gets their own rather than sharing a stranger's hall.
    /// </summary>
    public static uint GroupKey(Player player, InstanceScope scope) => scope switch
    {
        InstanceScope.Fellowship => player.Fellowship?.FellowshipLeaderGuid ?? player.Guid.Full,
        InstanceScope.Allegiance => player.Allegiance?.MonarchId ?? player.Guid.Full,
        _ => player.Guid.Full,
    };

    /// <summary>A human-readable description of who a copy belongs to, for messages.</summary>
    public static string GroupDescription(Player player, InstanceScope scope) => scope switch
    {
        InstanceScope.Fellowship => player.Fellowship is not null
            ? $"your fellowship ({player.Fellowship.FellowshipName})"
            : "you alone - you are not in a fellowship",
        InstanceScope.Allegiance => player.Allegiance is not null
            ? "your allegiance"
            : "you alone - you are not sworn to an allegiance",
        _ => "you",
    };

    public static void Assign(Player player, ushort landblock, int copy, InstanceScope scope)
    {
        var key = GroupKey(player, scope);

        if (copy <= 0)
            _assigned.TryRemove((key, landblock), out _);
        else
            _assigned[(key, landblock)] = copy;
    }

    /// <summary>
    /// Players who have asked, once, to arrive on the master landblock rather than be
    /// handed a copy.
    ///
    /// An instanced dungeon copies you on entry by design, which is exactly wrong for
    /// the three cases that mean to reach the original: /inst master, /inst leave,
    /// and an admin teleporting to someone who is standing in the master. Clearing the
    /// assignment is not enough on its own - the teleport that follows sees no
    /// assignment, concludes you are arriving fresh, and issues a new copy.
    ///
    /// Keyed by guid rather than held in a thread-local because the teleport is queued:
    /// it runs on the world thread some ticks later, and a thread-local set now would
    /// either be gone by then or leak into an unrelated player's arrival.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, (ushort Landblock, int Copy, DateTime When)> _forced = new();

    /// <summary>How long an unfulfilled request to reach a particular copy stays live.</summary>
    private static readonly TimeSpan ForcedLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// "Put me in this exact copy of this landblock, whatever the usual rules say."
    /// Copy 0 means the master.
    ///
    /// Needed because Resolve's first rule is that an object already in a copy stays in
    /// it, and that rule is right for every case except the handful where somebody has
    /// deliberately named a destination: /inst master, /inst leave, /inst enter n,
    /// /inst goto, and an admin following a player with /teleto. Without this, all of
    /// those silently leave you exactly where you were standing - the teleport runs, the
    /// position is identical, and stay-put wins.
    /// </summary>
    public static void Force(Player player, ushort landblock, int copy) =>
        _forced[player.Guid.Full] = (landblock, copy, DateTime.UtcNow);

    /// <summary>
    /// Peeked, not consumed. A teleport asks the routing question twice - once in
    /// Player.Teleport and again in the physics relocation - and it is the SECOND
    /// question that decides where the player lands. Consuming on the first left the
    /// relocation with nothing to go on.
    ///
    /// Expires on its own so a teleport that never completes cannot leave a player
    /// permanently pinned.
    /// </summary>
    public static bool HasForced(Player player, ushort landblock, out int copy)
    {
        copy = 0;

        if (!_forced.TryGetValue(player.Guid.Full, out var forced))
            return false;

        if (DateTime.UtcNow - forced.When > ForcedLifetime)
        {
            _forced.TryRemove(player.Guid.Full, out _);
            return false;
        }

        if (forced.Landblock != landblock)
            return false;

        copy = forced.Copy;
        return true;
    }

    public static void ClearForced(Player player) => _forced.TryRemove(player.Guid.Full, out _);

    /// <summary>Drops every assignment pointing at a copy that has been closed.</summary>
    public static void ForgetCopy(ushort landblock, int copy)
    {
        foreach (var (key, value) in _assigned.ToList())
        {
            if (key.Landblock == landblock && value == copy)
                _assigned.TryRemove(key, out _);
        }
    }

    public static void ForgetAll() => _assigned.Clear();

    /// <summary>The copy this player's group owns for a landblock, or 0 for ACE's own.</summary>
    public static int CopyFor(Player player, ushort landblock)
    {
        var scope = Mod.ScopeFor(landblock);

        return _assigned.TryGetValue((GroupKey(player, scope), landblock), out var copy) ? copy : 0;
    }

    /// <summary>
    /// The copy a world object should resolve to for a target landblock.
    ///
    /// Order matters. An object already standing in a copy of the same landblock stays
    /// where it is - that single rule is what keeps movement, combat and broadcast
    /// correct without any of them knowing instances exist. Only after that does a
    /// player's assignment get a say, and only then the ambient tick.
    /// </summary>
    public static int Resolve(WorldObject? wo, ushort landblock)
    {
        if (wo is not null)
        {
            // Before the stay-put rule, because the whole point of asking for the master
            // is to stop staying put. Cleared once the player is actually out of a copy,
            // which is what makes this self-terminating rather than sticky.
            if (wo is Player heading && HasForced(heading, landblock, out var forced))
            {
                // Cleared once they are actually there, which is what makes this
                // self-terminating rather than a pin that outlives its purpose.
                //
                // "There" cannot be CopyOf == forced, because CopyOf answers 0 for the
                // master AND for a closed copy nobody owns. A player still standing in
                // the block they were being moved OUT of therefore looked like they had
                // arrived, the flag cleared a step early, and the next question routed
                // them somewhere else entirely.
                var arrived = forced > 0
                    ? InstanceWorld.CopyOf(heading.CurrentLandblock) == forced
                      && InstanceWorld.IsOurs(heading.CurrentLandblock)
                    : !InstanceWorld.IsOurs(heading.CurrentLandblock)
                      && !InstanceWorld.IsOrphaned(heading.CurrentLandblock);

                if (arrived)
                    ClearForced(heading);

                return forced;
            }

            var current = InstanceWorld.CopyOf(wo.CurrentLandblock);

            if (current > 0 && wo.CurrentLandblock!.Id.LandblockX == (byte)(landblock >> 8)
                            && wo.CurrentLandblock!.Id.LandblockY == (byte)(landblock & 0xFF))
                return current;

            if (wo is Player player)
            {
                var assigned = CopyFor(player, landblock);

                if (assigned > 0)
                    return assigned;
            }

            // A projectile is created before it belongs anywhere: no landblock of its
            // own, and not a player, so nothing else here can place it. Its shooter can.
            // Without this, an arrow loosed inside a copy is born in the master and flies
            // through a dungeon nobody is standing in.
            var source = InstanceWorld.CopyOf(wo.ProjectileSource?.CurrentLandblock);

            if (source > 0 && wo.ProjectileSource!.CurrentLandblock!.Id.LandblockX == (byte)(landblock >> 8)
                           && wo.ProjectileSource!.CurrentLandblock!.Id.LandblockY == (byte)(landblock & 0xFF))
                return source;
        }

        var ambient = InstanceWorld.CopyOf(Ambient);

        if (ambient > 0 && Ambient!.Id.LandblockX == (byte)(landblock >> 8)
                        && Ambient!.Id.LandblockY == (byte)(landblock & 0xFF))
            return ambient;

        return 0;
    }
}
