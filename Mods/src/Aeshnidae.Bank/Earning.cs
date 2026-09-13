namespace Aeshnidae.Bank;

/// <summary>
/// Where radiance and resonance come from, and the buffer that keeps earning them
/// from costing anything on the world thread.
///
/// The problem this solves. Awards happen on a landblock thread, in the middle of a
/// tick: every experience grant for radiance, a quest stamp for resonance. BankDb.TryAdjust
/// takes a process-wide lock and does a MySQL round trip inside a transaction, which
/// is entirely reasonable for a player typing /bank deposit and entirely unreasonable
/// on every monster death on the shard. Doing it inline would put database latency
/// inside the tick loop and serialise every landblock behind one lock.
///
/// So awards land in memory and are written out by a background flush. The player is
/// told immediately and reads from balance + pending everywhere, so nothing looks lost
/// in between - see PendingFor, which BankService folds into every balance it reports.
///
/// What this costs: a hard crash loses at most FlushSeconds of unflushed earnings.
/// That is the deliberate trade, it is bounded, and a clean shutdown flushes. If you
/// would rather have durability than tick latency, set FlushSeconds to 0 and awards
/// are written synchronously instead.
/// </summary>
public static class Earning
{
    /// <summary>
    /// (account, currency) -> earned but not yet written. Read by PendingFor on the
    /// command threads and mutated by the award paths on landblock threads, hence the
    /// concurrent dictionary and the atomic AddOrUpdate rather than a read-modify-write.
    /// </summary>
    private static readonly ConcurrentDictionary<(uint Account, CurrencyKind Kind), long> _pending = new();

    /// <summary>
    /// player guid -> radiance earned since their last chat line.
    ///
    /// Separate from _pending, which is keyed by ACCOUNT: the balance belongs to the
    /// account but the sentence belongs to the character who swung, and on a shared
    /// account those are not the same thing.
    /// </summary>
    private static readonly ConcurrentDictionary<uint, long> _unannounced = new();

    private static Timer? _flushTimer;

    private static Timer? _announceTimer;

    public static void Start()
    {
        var flush = Mod.Settings.FlushSeconds;

        if (flush > 0)
        {
            var period = TimeSpan.FromSeconds(flush);
            _flushTimer = new Timer(_ => Flush(), null, period, period);
        }
        else
        {
            // Synchronous mode writes each award as it happens, so there is nothing to
            // flush - but History.Prune rides this tick, and without it the earning
            // ledgers of everyone who ever logged in would be kept for the life of the
            // process. A slow maintenance timer covers it.
            var period = TimeSpan.FromMinutes(5);
            _flushTimer = new Timer(_ => History.Prune(), null, period, period);
        }

        var rollup = Mod.Settings.AnnounceRollupSeconds;

        if (rollup > 0)
        {
            var period = TimeSpan.FromSeconds(rollup);
            _announceTimer = new Timer(_ => DrainAnnouncements(), null, period, period);
        }
    }

    /// <summary>Flushes and stops. Called on unload so a hot reload does not drop earnings.</summary>
    public static void Stop()
    {
        _flushTimer?.Dispose();
        _flushTimer = null;

        _announceTimer?.Dispose();
        _announceTimer = null;

        // Unsent chat lines are dropped rather than delivered. The currency itself is
        // in _pending and is about to be written; the sentence about it is worth
        // nothing during a shutdown or reload.
        _unannounced.Clear();

        Flush();
    }

    // --------------------------------------------------------------- announcing

    /// <summary>
    /// Queues, or immediately sends, the chat line for radiance earned.
    ///
    /// Called from a landblock thread, so it does no work beyond an atomic add when
    /// rollup is on.
    /// </summary>
    public static void AnnounceRadiance(Player player, long amount)
    {
        if (!Mod.Settings.AnnounceRadiance || amount <= 0)
            return;

        if (Mod.Settings.AnnounceRollupSeconds <= 0)
        {
            player.SendMessage($"You gain {amount:N0} Radiance.");
            return;
        }

        _unannounced.AddOrUpdate(player.Guid.Full, amount, (_, current) => current + amount);
    }

    /// <summary>
    /// Sends one line per player for everything they have earned since the last pass.
    ///
    /// Entries are removed before the message is built, so radiance earned during this
    /// pass accumulates into the next line rather than being announced twice or lost.
    /// A player who logged out in between is simply dropped - they already have the
    /// currency, and there is nobody to tell.
    /// </summary>
    private static void DrainAnnouncements()
    {
        foreach (var guid in _unannounced.Keys.ToList())
        {
            if (!_unannounced.TryRemove(guid, out var amount) || amount <= 0)
                continue;

            try
            {
                PlayerManager.GetOnlinePlayer(guid)?.SendMessage($"You gain {amount:N0} Radiance.");
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] could not announce radiance to {guid:X8}: {ex.Message}",
                               ModManager.LogLevel.Warn);
            }
        }
    }

    /// <summary>Earned but not yet written, for one account and currency.</summary>
    public static long PendingFor(uint accountId, CurrencyKind kind) =>
        _pending.TryGetValue((accountId, kind), out var amount) ? amount : 0;

    /// <summary>Everything pending for an account, so a statement can be complete.</summary>
    public static Dictionary<CurrencyKind, long> PendingFor(uint accountId)
    {
        var result = new Dictionary<CurrencyKind, long>();

        foreach (var (key, amount) in _pending)
        {
            if (key.Account == accountId && amount != 0)
                result[key.Kind] = amount;
        }

        return result;
    }

    /// <summary>
    /// Credits an account. Safe to call from any thread, including a landblock tick.
    ///
    /// Returns silently for a non-positive amount rather than treating it as a debit -
    /// every caller here is an award, and a rounding-down to zero is the normal case
    /// for a cheap kill rather than something to report.
    /// </summary>
    public static void Award(uint accountId, CurrencyKind kind, long amount)
    {
        if (amount <= 0)
            return;

        if (Mod.Settings.FlushSeconds <= 0)
        {
            BankDb.TryAdjust(accountId, kind, amount, out _);
            return;
        }

        _pending.AddOrUpdate((accountId, kind), amount, (_, current) => current + amount);
    }

    /// <summary>
    /// Writes every pending balance out.
    ///
    /// Each entry is REMOVED before it is written, so a concurrent award during the
    /// flush accumulates into a fresh entry rather than being overwritten by this one.
    /// If the write then fails, the amount is added back rather than dropped - losing
    /// earned currency silently is the one outcome worth extra code to avoid.
    /// </summary>
    public static void Flush()
    {
        // Cheap, and this is the only recurring tick the mod has, so the earning
        // history is swept from here rather than given a timer of its own.
        History.Prune();

        if (!BankDb.Ready)
            return;

        foreach (var key in _pending.Keys.ToList())
        {
            if (!_pending.TryRemove(key, out var amount) || amount == 0)
                continue;

            try
            {
                if (!BankDb.TryAdjust(key.Account, key.Kind, amount, out _))
                    throw new InvalidOperationException("the balance update matched no rows");
            }
            catch (Exception ex)
            {
                _pending.AddOrUpdate(key, amount, (_, current) => current + amount);

                ModManager.Log($"[{Mod.Name}] could not bank {amount:N0} {key.Kind} for account " +
                               $"{key.Account}, holding it for the next flush: {ex.Message}",
                               ModManager.LogLevel.Warn);
            }
        }
    }

    // ----------------------------------------------------------------- resonance

    /// <summary>
    /// Resonance for completing a quest.
    ///
    /// Flat per completion, because a quest has no equivalent of XpOverride to scale
    /// from - there is no field in the registry saying what a quest was worth. Per-quest
    /// values belong in ResonanceOverrides once you know which quests deserve them.
    /// </summary>
    public static long ResonanceFor(string questName)
    {
        var overrides = Mod.Settings.ResonanceOverrides;

        if (overrides is not null && overrides.TryGetValue(questName, out var specific))
            return specific;

        return Mod.Settings.ResonancePerQuest;
    }
}
