namespace Aeshnidae.FairPlay;

/// <summary>
/// Admin-only: these expose which accounts share an address, which is exactly the
/// sort of thing that should not be readable by players.
/// </summary>
public static class Commands
{
    [CommandHandler("fairplay", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Fair-play rules: status, shared addresses, per-account history.",
        "/fairplay [ shared | linked | account <name> | ip <address> ]")]
    public static void HandleFairPlay(Session session, params string[] parameters)
    {
        try
        {
            var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

            switch (verb)
            {
                case "shared": Shared(session); return;
                case "linked": Linked(session); return;
                case "account" when parameters.Length > 1: Account(session, parameters[1]); return;
                case "ip" when parameters.Length > 1: Address(session, parameters[1]); return;
                default: Status(session); return;
            }
        }
        catch (Exception ex)
        {
            Reply(session, $"/fairplay failed: {ex.Message}");
            ModManager.Log($"[{Mod.Name}] /fairplay failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    private static void Status(Session? session)
    {
        var s = Mod.Settings;
        var text = new StringBuilder()
            .AppendLine($"{Mod.Name} v{Mod.Container?.Meta.Version ?? "?"} - {Mod.Container?.Status.ToString() ?? "not loaded"}")
            .AppendLine($"Enabled: {s.Enabled}")
            .AppendLine($"Max online per address: {s.MaxConcurrentPerAddress} (exempt at {s.ExemptLevel}+)")
            .AppendLine($"Character creation cap: {PropertyManager.GetLong("max_chars_per_account").Item}")
            .AppendLine($"One char outside Marketplace: {s.OneCharacterOutsideMarketplace} (landblock 0x{s.Marketplace.Landblock})")
            .AppendLine($"Same-IP allegiance blocked: {s.BlockSameIpAllegiance}")
            .AppendLine($"Login history: {Mod.Logins?.KnownAccounts ?? 0} accounts across {Mod.Logins?.KnownAddresses ?? 0} addresses")
            .AppendLine($"Since start - logins {Mod.LoginsRecorded}, bounces {Mod.Bounces}, forced logouts {Mod.LogoutsForced}, pledges blocked {Mod.PledgesBlocked}, linked trades {Mod.LinkedTrades}, ground transfers {Mod.GroundTransfers}, vendor handovers {Mod.VendorHandovers}");

        var shared = Mod.Logins?.Shared(s.FlagAccountsPerAddress) ?? new();
        if (shared.Count > 0)
            text.AppendLine($"Addresses at/over the flag threshold ({s.FlagAccountsPerAddress}): {shared.Count}  -> /fairplay shared");

        Reply(session, text.ToString());
    }

    private static void Linked(Session? session)
    {
        var households = Mod.Logins?.Households() ?? new();

        if (households.Count == 0)
        {
            Reply(session, "No accounts are linked - none have shared an address yet.");
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"Linked households ({households.Count}) - accounts that have shared an address:");

        foreach (var h in households.Take(20))
            text.AppendLine($"  [{h.Count}] {string.Join(", ", h.Take(10))}");

        text.AppendLine("Rules apply per household, so a VPN on one account does not unlink it.");
        text.AppendLine("Use NeverLink in Settings.json to pin an account to a household of one.");

        Reply(session, text.ToString());
    }

    private static void Shared(Session? session)
    {
        var shared = Mod.Logins?.Shared() ?? new();

        if (shared.Count == 0)
        {
            Reply(session, "No address has been used by more than one account.");
            return;
        }

        var text = new StringBuilder().AppendLine($"Addresses used by more than one account ({shared.Count}):");

        foreach (var (address, accounts) in shared.Take(25))
            text.AppendLine($"  {address,-16} {accounts.Count}: {string.Join(", ", accounts.Take(8))}");

        Reply(session, text.ToString());
    }

    private static void Account(Session? session, string account)
    {
        var addresses = Mod.Logins?.Addresses(account) ?? new();

        if (addresses.Count == 0)
        {
            Reply(session, $"No login history for account \"{account}\".");
            return;
        }

        var text = new StringBuilder().AppendLine($"{account} has logged in from {addresses.Count} address(es):");
        foreach (var a in addresses.Take(30))
            text.AppendLine($"  {a}");

        Reply(session, text.ToString());
    }

    private static void Address(Session? session, string address)
    {
        var accounts = Mod.Logins?.Accounts(address) ?? new();

        if (accounts.Count == 0)
        {
            Reply(session, $"No login history for address {address}.");
            return;
        }

        var text = new StringBuilder().AppendLine($"{address} has been used by {accounts.Count} account(s):");
        foreach (var a in accounts.Take(30))
            text.AppendLine($"  {a}");

        Reply(session, text.ToString());
    }

    [CommandHandler("fairplay-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the fair-play mod, re-reading Settings.json.",
        "/fairplay-reload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;

        if (container is null)
        {
            Reply(session, $"{Mod.Name} is not loaded.");
            return;
        }

        container.Restart();
        Reply(session, $"{Mod.Name} restarted.");
    }

    /// <summary>
    /// The client renders an embedded newline as a music note, so a multi-line
    /// message has to go out as one SendMessage per line.
    /// </summary>
    private static void Reply(Session? session, string message)
    {
        if (session?.Player is null)
        {
            ModManager.Log(message);
            return;
        }

        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                session.Player.SendMessage(text);
        }
    }
}
