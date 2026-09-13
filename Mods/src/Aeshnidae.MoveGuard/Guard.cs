using ACE.Server.Network.Enum;
using ACE.Server.Network.Sequence;
using ACE.Server.Physics.Animation;

using PhysicsPosition = ACE.Server.Physics.Common.Position;

namespace Aeshnidae.MoveGuard;

/// <summary>
/// The judgement. Called for every position the client asks the server to adopt.
///
/// What ACE does on its own, and why it is not enough. Player.UpdatePlayerPosition
/// has a speed check, but it fires only when one update crosses more than a whole
/// landblock (192 units) - inside that, any distance is taken at face value. It also
/// runs a physics transition from the old position to the new one, and when that
/// transition cannot get there (a wall) it logs at Debug level and forces the requested
/// position anyway. A blink plugin lives entirely inside those two gaps: it writes a
/// position into the client, the client reports it, and the server obliges.
///
/// What this does instead. The distance a client may move between two accepted
/// positions is bounded by that player's own top speed times the time elapsed, and
/// separately the same physics transition ACE runs is asked how far it actually got.
/// A move outside either bound is a strike; in Enforce mode it is also refused, and
/// the client is told to adopt the server's position - which is where everyone else
/// already sees you, where monsters hit you, and what every distance check uses.
/// </summary>
internal static class Guard
{
    /// <summary>
    /// Harmony prefix body. Returning true lets ACE's method run; returning false
    /// skips it and hands back <paramref name="result"/>.
    /// </summary>
    public static bool Judge(Player player, Position? newPosition, bool forceUpdate, ref bool result)
    {
        var s = Mod.Settings;
        var mode = s.ModeValue;

        if (mode == Mode.Off || newPosition is null)
            return true;

        // Server-initiated moves: the teleport landing itself (forceUpdate) and the
        // packets the client keeps sending from the old spot while it loads.
        if (forceUpdate || player.Teleporting)
            return true;

        var state = Tracker.For(player);
        var now = DateTime.UtcNow;

        // First position after login: nothing to measure from yet.
        if (state.LastAccepted == default)
        {
            state.LastAccepted = now;
            return true;
        }

        if ((now - player.LastTeleportTime).TotalSeconds < s.TeleportGraceSeconds)
            return true;

        var from = player.Location;

        if (from is null)
            return true;

        // A move between landblocks where either end is indoors is ACE's to judge -
        // its ValidateMovement refuses those outright, and dungeon landblock ids do
        // not sit on the outdoor grid, so a distance between them means nothing.
        if (from.Landblock != newPosition.Landblock &&
            ((from.Cell & 0xFFFF) >= 0x100 || (newPosition.Cell & 0xFFFF) >= 0x100))
            return true;

        var elapsed = Math.Max(0.0, (now - state.LastAccepted).TotalSeconds);
        var distance = (double)from.Distance2D(newPosition);
        var ceiling = Speed.CeilingFor(player);
        var allowed = ceiling * s.SpeedTolerance * Math.Min(elapsed, s.MaxCatchUpSeconds) + s.SlackUnits;

        Violation? violation = null;

        if (distance > allowed)
        {
            violation = new("speed", distance, allowed, elapsed, ceiling, newPosition.ToLOCString());
        }
        else if (s.CheckGeometry && distance > 0.5)
        {
            var shortfall = Geometry.Shortfall(player, newPosition, distance);

            if (shortfall > s.GeometryTolerance)
                violation = new("geometry", shortfall, s.GeometryTolerance, elapsed, ceiling, newPosition.ToLOCString());
        }

        if (violation is null)
            return true;

        return Strike(player, state, violation, now, mode, ref result);
    }

    /// <summary>
    /// Records the strike, reports it, and - when enforcing - refuses the move.
    /// </summary>
    private static bool Strike(Player player, PlayerState state, Violation v, DateTime now, Mode mode, ref bool result)
    {
        var s = Mod.Settings;

        state.Last = v;

        if (v.Kind == "speed")
        {
            state.SpeedViolations++;
            Interlocked.Increment(ref Mod.SpeedViolations);
        }
        else
        {
            state.GeometryViolations++;
            Interlocked.Increment(ref Mod.GeometryViolations);
        }

        var inWindow = state.AddStrike(now, s.StrikeWindowSeconds);

        if (inWindow < state.StrikesAtLastAudit)
            state.StrikesAtLastAudit = 0;   // the window emptied out; start counting toward a fresh post

        var exempt = s.IsExempt(player);
        var enforce = mode == Mode.Enforce && !exempt && (v.Kind == "speed" || s.EnforceGeometry);
        var outcome = enforce ? "rejected" : exempt ? "logged (staff)" : "logged";

        // The log line is throttled per player; the audit channel is the signal. A
        // systematic false positive - say a wrong run-speed reading - would otherwise
        // write one line per moving player per second, and bury the real offenders.
        if (inWindow <= s.LogPerWindow)
            ModManager.Log($"[{Mod.Name}] {player.Name}: {Describe(v)} - {outcome}", ModManager.LogLevel.Warn);
        else if (inWindow == s.LogPerWindow + 1)
            ModManager.Log($"[{Mod.Name}] {player.Name}: further strikes this window not logged individually " +
                           $"(/moveguard check {player.Name})", ModManager.LogLevel.Warn);

        if (s.AuditAfterStrikes > 0 && inWindow >= s.AuditAfterStrikes && inWindow - state.StrikesAtLastAudit >= s.AuditAfterStrikes)
        {
            state.StrikesAtLastAudit = inWindow;

            PlayerManager.BroadcastToAuditChannel(null,
                $"[MoveGuard] {player.Name} ({player.Account?.AccountName ?? "?"}): {inWindow} rejected-or-flagged " +
                $"positions in the last {s.StrikeWindowSeconds}s, latest {Describe(v)} - {outcome}");
        }

        if (!enforce)
            return true;

        state.Rejections++;
        Interlocked.Increment(ref Mod.Rejections);
        var rejectedInWindow = state.AddRejection(now, s.StrikeWindowSeconds);

        // Restart the clock. Without this a client that ignores the snap-back and keeps
        // reporting the same far position is simply waited out: the allowance grows with
        // elapsed time until it covers the blink, and the move is accepted with one
        // strike to show for it. Measuring from the rejection instead means a position
        // the server refused stays refused until the client comes back within reach.
        // The retail client adopts the forced position within a tick, so an honest
        // player's next report is from where the server put them and passes anyway.
        state.LastAccepted = now;

        // Rubber band. Location is left exactly where the server had it; bumping the
        // force-position sequence is what makes the client adopt a position for its
        // own character instead of ignoring it. This is the same pair of calls ACE's
        // own z-hack response uses.
        player.Sequences.GetNextSequence(SequenceType.ObjectForcePosition);
        player.SendUpdatePosition();

        if (s.TellPlayer)
            player.SendMessage(s.PlayerMessage);

        if (s.KickAfterStrikes > 0 && rejectedInWindow >= s.KickAfterStrikes && !state.Kicked)
        {
            state.Kicked = true;
            Interlocked.Increment(ref Mod.Kicks);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[MoveGuard] {player.Name} ({player.Account?.AccountName ?? "?"}) disconnected after {rejectedInWindow} rejected positions in {s.StrikeWindowSeconds}s");

            player.Session?.Terminate(SessionTerminationReason.AccountBooted,
                new GameMessageBootAccount(" for movement the server could not accept"));
        }

        result = false;
        return false;
    }

    public static string Describe(Violation v) => v.Kind == "speed"
        ? $"moved {v.Distance:F1} units in {v.Elapsed:F2}s, allowed {v.Allowed:F1} (top speed {v.Ceiling:F1} u/s) to {v.Where}"
        : $"physics reached {v.Distance:F1} units short of the requested position (tolerance {v.Allowed:F1}) at {v.Where}";
}

/// <summary>
/// A player's real top speed, in units per second, from the same two inputs the
/// client uses: the run animation's distance per second, and the run-rate multiplier
/// ACE derives from the Run skill (capped at 4.5 from skill 800).
/// </summary>
internal static class Speed
{
    public static double CeilingFor(Player player)
    {
        var s = Mod.Settings;

        double anim = 0;

        try { anim = MotionTable.GetRunSpeed(player.MotionTableId); }
        catch { /* fall through to the fallback */ }

        if (anim <= 0)
            anim = s.FallbackRunSpeed;

        // Burden and exhaustion only ever LOWER the rate the client moves at, and the
        // server's view of both can lag the client's by a tick - so they are left out,
        // and this is an upper bound rather than the exact figure.
        var runSkill = player.GetCreatureSkill(Skill.Run)?.Current ?? 0;
        var rate = MovementSystem.GetRunRate(0f, (int)runSkill, 1.0f);

        return anim * rate * (player.ObjScale ?? 1.0f);
    }
}

/// <summary>
/// Asks the physics engine the question ACE asks and then ignores: starting from where
/// the server has you, can you get to where the client says you are?
/// </summary>
internal static class Geometry
{
    /// <summary>
    /// How far short of the requested position the collision transition stops, in
    /// units. 0 means it got there. Physics slides along obstacles rather than failing
    /// outright, so a wall shows up as a large shortfall; a null transition (nowhere
    /// valid at all) is reported as the whole distance.
    /// </summary>
    public static double Shortfall(Player player, Position requested, double requestedDistance)
    {
        try
        {
            var phys = player.PhysicsObj;

            if (phys?.CurCell is null)
                return 0;

            var target = new PhysicsPosition(requested);
            var transit = phys.transition(phys.Position, target, false);

            if (transit?.SpherePath?.CurPos is null)
                return requestedDistance;

            return transit.SpherePath.CurPos.Distance(target);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] geometry check failed for {player.Name}: {ex.Message}", ModManager.LogLevel.Warn);
            return 0;   // never strike on our own error
        }
    }
}
