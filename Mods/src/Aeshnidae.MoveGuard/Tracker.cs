namespace Aeshnidae.MoveGuard;

/// <summary>What one rejected or reported position looked like, for the log and the audit line.</summary>
internal sealed record Violation(string Kind, double Distance, double Allowed, double Elapsed, double Ceiling, string Where);

/// <summary>Per-player bookkeeping. One per online character, dropped at logout.</summary>
internal sealed class PlayerState
{
    /// <summary>
    /// When the server last took a position from this client. The allowance for the
    /// next move is measured from here, not from the previous packet, so a rejected
    /// position does not advance the clock.
    /// </summary>
    public DateTime LastAccepted;

    /// <summary>
    /// Strike times, oldest first. Mutated on the landblock thread and read by
    /// /moveguard on a command thread, so every touch takes <see cref="Gate"/> -
    /// a Queue is not safe to enumerate while another thread dequeues.
    /// </summary>
    public readonly Queue<DateTime> Strikes = new();

    public readonly object Gate = new();

    public long SpeedViolations, GeometryViolations, Rejections;

    public Violation? Last;

    /// <summary>Strike count at the last audit post, so posts go out per threshold rather than per strike.</summary>
    public int StrikesAtLastAudit;

    public bool Kicked;

    /// <summary>Rejection times, a subset of Strikes. Only these count toward a kick.</summary>
    public readonly Queue<DateTime> RejectionTimes = new();

    /// <summary>Records a strike and returns how many fall inside the window.</summary>
    public int AddStrike(DateTime now, int windowSeconds)
    {
        lock (Gate)
        {
            Strikes.Enqueue(now);
            return Prune(Strikes, now, windowSeconds);
        }
    }

    /// <summary>Records a rejection and returns how many fall inside the window.</summary>
    public int AddRejection(DateTime now, int windowSeconds)
    {
        lock (Gate)
        {
            RejectionTimes.Enqueue(now);
            return Prune(RejectionTimes, now, windowSeconds);
        }
    }

    /// <summary>Strikes inside the window, for readers on other threads (/moveguard).</summary>
    public int CountInWindow(DateTime now, int windowSeconds)
    {
        lock (Gate)
        {
            var cutoff = now.AddSeconds(-windowSeconds);
            return Strikes.Count(t => t >= cutoff);
        }
    }

    private static int Prune(Queue<DateTime> q, DateTime now, int windowSeconds)
    {
        var cutoff = now.AddSeconds(-windowSeconds);

        while (q.Count > 0 && q.Peek() < cutoff)
            q.Dequeue();

        return q.Count;
    }
}

internal static class Tracker
{
    /// <summary>
    /// player guid -> state. Each player's entry is touched from that player's
    /// landblock thread and read by /moveguard from a command thread, hence the
    /// concurrent map; the state inside is only ever mutated from the one thread.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, PlayerState> _states = new();

    public static PlayerState For(Player player) =>
        _states.GetOrAdd(player.Guid.Full, _ => new PlayerState());

    public static PlayerState? Peek(Player player) =>
        _states.TryGetValue(player.Guid.Full, out var s) ? s : null;

    public static void Forget(Player player) =>
        _states.TryRemove(player.Guid.Full, out _);

    public static void Clear() => _states.Clear();

    public static int Count => _states.Count;

    /// <summary>Every tracked player with at least one strike ever, worst first.</summary>
    public static IEnumerable<(uint Guid, PlayerState State)> Offenders() =>
        _states.Where(kv => kv.Value.SpeedViolations + kv.Value.GeometryViolations > 0)
               .OrderByDescending(kv => kv.Value.SpeedViolations + kv.Value.GeometryViolations)
               .Select(kv => (kv.Key, kv.Value));
}
