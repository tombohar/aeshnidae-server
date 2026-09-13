namespace Aeshnidae.Enlightenment;

/// <summary>
/// Stops one player enlightening twice from a single set of requirements.
///
/// The design brief asked for an NPC specifically because a command had been
/// abusable, so it is worth being precise about what that buys. ACE has no
/// enlightenment command - the only route in is EmoteType.Enlightenment (9001),
/// which only the Font's emote chain raises - so the NPC route is already the only
/// route, and this mod adds no command that grants anything.
///
/// What the NPC does not fix by itself is re-entry: an emote chain that reaches
/// two InqYesNo confirmations can run the payload twice. In practice the level
/// reset closes that window - the second run re-checks requirements and finds a
/// level 1 character - but that is a consequence of ResetLevel being on, not a
/// guarantee. Turn ResetLevel off and the only thing standing between a player and
/// a title farm is this class. So it is explicit rather than incidental:
///
///   - Running: held for the duration of one ritual, so a re-entrant call during
///     the chain is refused outright.
///   - Cooldown: held for ReentryGuardSeconds afterwards, covering two
///     confirmations answered a tick apart.
///
/// Keyed on character guid rather than account, and dropped on logout, so it never
/// grows without bound.
/// </summary>
public static class Guard
{
    private static readonly ConcurrentDictionary<uint, DateTime> Completed = new();
    private static readonly ConcurrentDictionary<uint, byte> Running = new();

    /// <summary>
    /// Claims the right to run a ritual for this player. False means one is already
    /// running - the caller must not proceed.
    /// </summary>
    public static bool TryEnter(Player player) => Running.TryAdd(player.Guid.Full, 0);

    /// <summary>Releases the claim and starts the cooldown. Always call from a finally.</summary>
    public static void Exit(Player player, bool completed)
    {
        Running.TryRemove(player.Guid.Full, out _);

        if (completed)
            Completed[player.Guid.Full] = DateTime.UtcNow;
    }

    public static bool IsCoolingDown(Player player, out double secondsRemaining)
    {
        secondsRemaining = 0;

        var seconds = Mod.Settings.ReentryGuardSeconds;

        if (seconds <= 0 || !Completed.TryGetValue(player.Guid.Full, out var last))
            return false;

        var elapsed = (DateTime.UtcNow - last).TotalSeconds;

        if (elapsed >= seconds)
        {
            Completed.TryRemove(player.Guid.Full, out _);
            return false;
        }

        secondsRemaining = Math.Ceiling(seconds - elapsed);
        return true;
    }

    public static void Forget(Player player)
    {
        Completed.TryRemove(player.Guid.Full, out _);
        Running.TryRemove(player.Guid.Full, out _);
    }

    /// <summary>Drops everything, for mod unload.</summary>
    public static void Clear()
    {
        Completed.Clear();
        Running.Clear();
    }
}
