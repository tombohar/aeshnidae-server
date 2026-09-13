namespace Aeshnidae.Bank;

/// <summary>
/// The per-character switch that sends earned luminance straight to the bank.
///
/// Off by default and opted into per character, because it is a real change to how
/// luminance behaves rather than a convenience: banked luminance is not subject to
/// MaximumLuminance, so a flagged character never loses an award to the cap and never
/// has to stop and deposit. That is the whole point, and it is also why it should be a
/// deliberate choice - the cap is a designed constraint in retail, and a character that
/// is meant to feel it should be able to.
///
/// Cached in memory, and that is not an optimisation. The flag is read inside
/// AddLuminance, which runs on a landblock thread in the middle of a tick, on every
/// kill that pays luminance. A database read there would put MySQL latency in the world
/// loop - the same reason Earning buffers its writes. So the flag is loaded once at
/// login and written through on change, and the hot path only ever reads a dictionary.
/// </summary>
public static class AutoBank
{
    /// <summary>The flag's name in the database.</summary>
    public const string Luminance = "AutoBankLuminance";

    /// <summary>character guid -> flagged. Absent means not loaded, which reads as off.</summary>
    private static readonly ConcurrentDictionary<uint, bool> _flags = new();

    /// <summary>
    /// Reads a character's flag out of the database and caches it. Called at login.
    ///
    /// A failure here leaves the character unflagged rather than throwing, so a database
    /// hiccup costs the convenience and not the login.
    /// </summary>
    public static void Load(Player player)
    {
        if (player is null || !BankDb.Ready)
            return;

        try
        {
            _flags[player.Guid.Full] = BankDb.GetFlag(player.Guid.Full, Luminance);
        }
        catch (Exception ex)
        {
            _flags[player.Guid.Full] = false;

            ModManager.Log($"[{Mod.Name}] could not read the auto-bank flag for {player.Name}, " +
                           $"treating it as off: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    public static void Forget(Player player)
    {
        if (player is not null)
            _flags.TryRemove(player.Guid.Full, out _);
    }

    /// <summary>
    /// Whether this character banks its luminance. Safe on a landblock thread - a
    /// dictionary lookup and nothing else.
    /// </summary>
    public static bool IsOn(Player player) =>
        Mod.Settings.AllowLuminanceAutoBank &&
        player is not null &&
        _flags.TryGetValue(player.Guid.Full, out var on) &&
        on;

    /// <summary>
    /// Moves enough luminance out of the bank onto the character to cover
    /// <paramref name="needed"/>, and reports whether they can now afford it.
    ///
    /// Topping the character up and letting ACE's own code spend it, rather than
    /// intercepting the spend itself, is what keeps this small: SpendLuminance still
    /// does the deducting and still sends the client update through its own private
    /// UpdateLuminance, so there is no second implementation of either to drift.
    ///
    /// Flushes first, because a player who has been killing things for the last few
    /// seconds has earnings still sitting in Earning's buffer, and refusing them a
    /// purchase they can demonstrably afford is worse than a rare database write on the
    /// world thread. Purchases are rare; kills are not.
    ///
    /// The character keeps anything topped up but not spent - if they walk away from
    /// the NPC without buying, that luminance is on them rather than in the bank until
    /// they deposit it. Untidy, but visible and never lost, which is the right way round.
    /// </summary>
    public static bool TopUp(Player player, long needed)
    {
        var onHand = player.AvailableLuminance ?? 0;

        if (onHand >= needed)
            return true;

        if (!BankDb.Ready || player.Account is null)
            return false;

        var shortfall = needed - onHand;

        try
        {
            Earning.Flush();

            var banked = BankDb.GetBalance(player.Account.AccountId, CurrencyKind.Luminance);

            if (banked < shortfall)
                return false;

            if (!BankDb.TryAdjust(player.Account.AccountId, CurrencyKind.Luminance, -shortfall, out _))
                return false;

            // Set directly rather than through GrantLuminance, which would clamp to
            // MaximumLuminance and destroy the difference. This is luminance on its way
            // out again a moment later, so the cap has no business in it.
            player.AvailableLuminance = onHand + shortfall;

            player.Session?.Network?.EnqueueSend(
                new GameMessagePrivateUpdatePropertyInt64(player, PropertyInt64.AvailableLuminance,
                                                          player.AvailableLuminance ?? 0));

            return true;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not draw {shortfall:N0} banked luminance for {player.Name}: {ex}",
                           ModManager.LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// Sets the flag and writes it through. Returns false if it could not be saved, in
    /// which case the cache is left alone rather than promising something that will not
    /// survive a relog.
    /// </summary>
    public static bool Set(Player player, bool enabled)
    {
        try
        {
            BankDb.SetFlag(player.Guid.Full, Luminance, enabled);
            _flags[player.Guid.Full] = enabled;
            return true;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not save the auto-bank flag for {player.Name}: {ex.Message}",
                           ModManager.LogLevel.Error);
            return false;
        }
    }
}
