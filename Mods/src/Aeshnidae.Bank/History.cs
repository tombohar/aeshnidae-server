namespace Aeshnidae.Bank;

/// <summary>
/// What each character has earned recently, for /earned.
///
/// Two questions are answered from the same ledger, and they want different shapes:
///
///   "How is this spot paying?"  - the rolling windows, bucketed by the minute.
///   "How did tonight go?"       - the session total, and the hourly rate it implies.
///
/// The windows are bucketed rather than logged because a player on a fast clearing loop
/// kills several things a second, and a list of individual awards would grow for as long
/// as they farmed. Sixty one-minute buckets per currency is a fixed size no matter how
/// hard anyone grinds, and asking for the last 30 minutes is a sum of 30 of them. The
/// cost is granularity: the oldest bucket in a window counts whole, so "the last 5
/// minutes" can span up to six minutes of wall clock. Invisible for reading an earn
/// rate, and it is what makes this cheap enough to update on every kill.
///
/// The session total is not bucketed at all - it is a running sum since login, so it
/// stays exact however long someone plays.
///
/// EARNED only. Radiance from kills and resonance from quests reach this; currency
/// received from another player does not, and Transfer deliberately does not call in
/// here. The question /earned answers is "what is this character producing", so money
/// somebody handed you would be a lie in it - two players could bounce the same balance
/// back and forth and both report a magnificent hourly rate.
///
/// In memory, and per session: logging out clears it. Restarting the server loses the
/// history, not the currency - balances are in the database and are unaffected.
/// </summary>
public static class History
{
    /// <summary>How many minute buckets are kept. The largest rolling window /earned reports.</summary>
    public const int Minutes = 60;

    /// <summary>
    /// Below this much play time an hourly projection is arithmetic rather than
    /// information - thirty seconds of a good pull extrapolates to a number nobody is
    /// going to make. /earned withholds the rate until a session is at least this old.
    /// </summary>
    public static readonly TimeSpan MinimumForProjection = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The currencies /earned reports, in the order it prints them.
    ///
    /// Luminance is ACE's own rather than one of ours, and it is here because it is
    /// earned from exactly the same two activities and is the third number anyone
    /// farming actually wants. It is tracked by watching what a player's balance really
    /// changes by, so unlike the other two it can report zero while awards are being
    /// made - which is precisely what you want to see when you are at the luminance cap
    /// and every kill is paying you nothing.
    /// </summary>
    public static readonly CurrencyKind[] Tracked =
    {
        CurrencyKind.Radiance,
        CurrencyKind.Resonance,
        CurrencyKind.Luminance,
    };

    private static int IndexOf(CurrencyKind kind) => Array.IndexOf(Tracked, kind);

    private class Ledger
    {
        /// <summary>[currency, minute] -> earned in that minute.</summary>
        private readonly long[,] _buckets = new long[Tracked.Length, Minutes];

        /// <summary>Which absolute minute each slot currently holds, so stale slots read as zero.</summary>
        private readonly long[] _stamp = new long[Minutes];

        private readonly long[] _session = new long[Tracked.Length];

        private readonly object _lock = new();

        public DateTime SessionStart { get; private set; } = DateTime.UtcNow;

        public long LastMinute { get; private set; }

        public void Record(int currency, long amount, long minute)
        {
            lock (_lock)
            {
                var slot = (int)(((minute % Minutes) + Minutes) % Minutes);

                // A slot still stamped with an older minute is a full lap behind, so its
                // contents belong to an hour ago. EVERY currency in the slot is cleared,
                // not just the one being written - resetting one would leave the others
                // holding stale value in a slot that now claims to be current.
                if (_stamp[slot] != minute)
                {
                    _stamp[slot] = minute;

                    for (var c = 0; c < Tracked.Length; c++)
                        _buckets[c, slot] = 0;
                }

                _buckets[currency, slot] += amount;
                _session[currency] += amount;

                LastMinute = minute;
            }
        }

        public long Sum(int currency, int windowMinutes, long now)
        {
            lock (_lock)
            {
                var oldest = now - windowMinutes + 1;
                var total = 0L;

                for (var i = 0; i < Minutes; i++)
                {
                    if (_stamp[i] >= oldest && _stamp[i] <= now)
                        total += _buckets[currency, i];
                }

                return total;
            }
        }

        public long SessionTotal(int currency)
        {
            lock (_lock)
                return _session[currency];
        }

        public void Reset()
        {
            lock (_lock)
            {
                Array.Clear(_buckets);
                Array.Clear(_stamp);
                Array.Clear(_session);

                // The clock restarts too. A reset that zeroed the totals but kept the
                // original login time would report an hourly rate against hours that no
                // longer have any earnings behind them, which reads as a collapse.
                SessionStart = DateTime.UtcNow;
                LastMinute = DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;
            }
        }
    }

    private static readonly ConcurrentDictionary<uint, Ledger> _ledgers = new();

    private static long NowMinute => DateTime.UtcNow.Ticks / TimeSpan.TicksPerMinute;

    /// <summary>
    /// Notes an award against the character who earned it.
    ///
    /// Per CHARACTER, not per account, and that is the useful answer rather than the
    /// consistent one: the balance is shared by every character on the account, but
    /// "what am I earning out here" is a question about the one holding the sword.
    /// </summary>
    public static void Record(Player player, CurrencyKind kind, long amount)
    {
        if (amount <= 0 || player is null)
            return;

        var currency = IndexOf(kind);

        if (currency < 0)
            return;   // not a currency /earned reports

        _ledgers.GetOrAdd(player.Guid.Full, _ => new Ledger()).Record(currency, amount, NowMinute);
    }

    /// <summary>Earned by this character over the last <paramref name="windowMinutes"/>.</summary>
    public static long Earned(Player player, CurrencyKind kind, int windowMinutes) =>
        _ledgers.TryGetValue(player.Guid.Full, out var ledger) && IndexOf(kind) >= 0
            ? ledger.Sum(IndexOf(kind), windowMinutes, NowMinute)
            : 0;

    /// <summary>Earned by this character since login, or since the last /earned reset.</summary>
    public static long Session(Player player, CurrencyKind kind) =>
        _ledgers.TryGetValue(player.Guid.Full, out var ledger) && IndexOf(kind) >= 0
            ? ledger.SessionTotal(IndexOf(kind))
            : 0;

    /// <summary>How long the current session has been running.</summary>
    public static TimeSpan SessionLength(Player player) =>
        _ledgers.TryGetValue(player.Guid.Full, out var ledger)
            ? DateTime.UtcNow - ledger.SessionStart
            : TimeSpan.Zero;

    /// <summary>
    /// Session earnings projected out to an hour.
    ///
    /// Deliberately measured against the WHOLE session rather than a recent window, so
    /// it includes the walking, the repairs and the deaths. A rate taken from five good
    /// minutes is a promise the next hour will not keep; a rate taken from two hours of
    /// actual play is what you would really make doing that again.
    ///
    /// Null until the session is old enough to mean anything.
    /// </summary>
    public static double? PerHour(Player player, CurrencyKind kind)
    {
        var elapsed = SessionLength(player);

        if (elapsed < MinimumForProjection)
            return null;

        return Session(player, kind) / elapsed.TotalHours;
    }

    /// <summary>Starts a fresh session. Login, and /earned reset.</summary>
    public static void StartSession(Player player)
    {
        if (player is not null)
            _ledgers.AddOrUpdate(player.Guid.Full, _ => new Ledger(), (_, existing) =>
            {
                existing.Reset();
                return existing;
            });
    }

    /// <summary>Forgets a character entirely. Logout.</summary>
    public static void EndSession(Player player)
    {
        if (player is not null)
            _ledgers.TryRemove(player.Guid.Full, out _);
    }

    /// <summary>
    /// Drops ledgers nothing has touched for a full hour.
    ///
    /// A safety net rather than the main path - logout removes a ledger outright. This
    /// catches the sessions that never got a clean logout, after a crash or a dropped
    /// connection, and it is why it keys off activity rather than trusting the hook.
    /// </summary>
    public static void Prune()
    {
        var cutoff = NowMinute - Minutes;

        foreach (var (guid, ledger) in _ledgers)
        {
            // Never prune someone who is standing right there. An idle player with an
            // hour-old ledger is still mid-session, and dropping it would restart their
            // session clock under them.
            if (ledger.LastMinute < cutoff && PlayerManager.GetOnlinePlayer(guid) is null)
                _ledgers.TryRemove(guid, out _);
        }
    }
}
