namespace Aeshnidae.Bank;

public record BankResult(bool Ok, string Message);

/// <summary>
/// Deposit / withdraw logic. Balances are debited before items are created and
/// re-credited if creation fails, so a full pack can never swallow a balance.
/// </summary>
public static class BankService
{
    public const long All = -1;

    // ---------------------------------------------------------------- deposit

    public static BankResult Deposit(Player player, CurrencyKind kind, long amount)
    {
        if (amount == 0 || (amount < 0 && amount != All))
            return new(false, "Amount must be a positive number, or 'all'.");

        // Earned currencies have no carried form, so there is nothing on the character
        // to deposit. Caught here rather than falling through to DepositKeys, which
        // would go looking for a Radiance item in the pack and report something
        // baffling about not finding one.
        if (Currencies.IsEarned(kind))
            return new(false, $"{Currencies.DisplayName(kind)} is banked the moment you earn it - " +
                              "there is nothing to deposit.");

        return kind switch
        {
            CurrencyKind.Pyreal => DepositPyreals(player, amount),
            CurrencyKind.Luminance => DepositLuminance(player, amount),
            _ => DepositKeys(player, kind, amount),
        };
    }

    /// <summary>
    /// /b d with nothing after it: everything in the pack that the bank takes, in one go.
    ///
    /// Each currency is tried on its own and the results are gathered, so a pack with
    /// pyreals and no keys deposits the pyreals and says nothing about keys. Only a
    /// currency that was actually there and then FAILED gets a line - the point of a
    /// sweep is that you do not have to know what you are carrying.
    /// </summary>
    public static BankResult DepositAll(Player player)
    {
        var lines = new List<string>();
        var deposited = 0;

        foreach (var kind in new[] { CurrencyKind.Pyreal, CurrencyKind.Luminance, CurrencyKind.LegendaryKey, CurrencyKind.SturdyIronKey })
        {
            // Skip what is not there, quietly. The individual deposits report "you have
            // no X" as a failure, which is right when you asked for X and wrong here.
            var carrying = kind switch
            {
                CurrencyKind.Pyreal => CarriedPyreals(player) > 0,
                CurrencyKind.Luminance => (player.MaximumLuminance ?? 0) > 0 && (player.AvailableLuminance ?? 0) > 0,
                _ => Currencies.AcceptedFor(kind).Keys.Any(w => player.GetInventoryItemsOfWCID(w).Any(i => (i.Structure ?? 0) > 0)),
            };

            if (!carrying)
                continue;

            var result = Deposit(player, kind, All);

            lines.Add(result.Message);

            if (result.Ok)
                deposited++;
        }

        if (lines.Count == 0)
            return new(false, "Nothing in your pack that the bank takes - no pyreals, luminance or keys.");

        return new(deposited > 0, string.Join("\n", lines));
    }

    private static BankResult DepositPyreals(Player player, long amount)
    {
        var carried = CarriedPyreals(player);

        if (amount == All)
            amount = carried;

        if (amount <= 0)
            return new(false, "You have no pyreals to deposit.");

        if (carried < amount)
            return new(false, $"You are only carrying {carried:N0} pyreals.");

        // Smallest denominations first, so we break as few large notes as possible.
        long collected = 0;

        foreach (var denom in Currencies.PyrealDenominations.Reverse())
        {
            if (collected >= amount)
                break;

            foreach (var stack in player.GetInventoryItemsOfWCID(denom.Wcid).ToList())
            {
                if (collected >= amount)
                    break;

                var size = stack.StackSize ?? 1;
                var stackValue = denom.Unit * size;
                var needed = amount - collected;

                if (stackValue <= needed)
                {
                    if (player.TryConsumeFromInventoryWithNetworking(stack, size))
                        collected += stackValue;

                    continue;
                }

                // Take as many whole notes from this stack as fit under the target.
                var take = (int)Math.Min(size, needed / denom.Unit);

                if (take > 0 && player.TryConsumeFromInventoryWithNetworking(stack, take))
                    collected += denom.Unit * take;

                // One more to cover any remainder; the excess is returned as change.
                if (collected < amount && take < size &&
                    player.TryConsumeFromInventoryWithNetworking(stack, 1))
                    collected += denom.Unit;
            }
        }

        if (collected < amount)
        {
            if (collected > 0)
                GiveOut(player, CurrencyKind.Pyreal, collected);

            return new(false, "Could not gather that many pyreals from your pack.");
        }

        var change = collected - amount;

        if (!BankDb.TryAdjust(player.Account.AccountId, CurrencyKind.Pyreal, amount, out var balance))
        {
            GiveOut(player, CurrencyKind.Pyreal, collected);
            return new(false, "The bank could not record that deposit. Nothing was taken.");
        }

        if (change > 0)
            GiveOut(player, CurrencyKind.Pyreal, change);

        var changeNote = change > 0 ? $" ({change:N0} returned as change)" : "";
        return new(true, $"Deposited {amount:N0} pyreals{changeNote}. Balance: {balance:N0}.");
    }

    private static BankResult DepositLuminance(Player player, long amount)
    {
        if (player.MaximumLuminance is null or 0)
            return new(false, "You are not attuned to luminance yet, so you cannot bank it.");

        var available = player.AvailableLuminance ?? 0;

        if (amount == All)
            amount = available;

        if (amount <= 0)
            return new(false, "You have no luminance to deposit.");

        if (available < amount)
            return new(false, $"You only have {available:N0} luminance.");

        if (!player.SpendLuminance(amount))
            return new(false, "Could not take that luminance.");

        if (!BankDb.TryAdjust(player.Account.AccountId, CurrencyKind.Luminance, amount, out var balance))
        {
            player.GrantLuminance(amount, XpType.Admin, ShareType.None);
            return new(false, "The bank could not record that deposit. Nothing was taken.");
        }

        return new(true, $"Deposited {amount:N0} luminance. Balance: {balance:N0}.");
    }

    /// <summary>
    /// Keys bank as charges. A key cannot be split, so whole keys are taken
    /// smallest-first and we stop before overshooting the requested amount.
    /// </summary>
    private static BankResult DepositKeys(Player player, CurrencyKind kind, long amount)
    {
        var label = Currencies.DisplayName(kind).ToLowerInvariant();

        var held = new List<(WorldObject Item, int Charges)>();

        foreach (var wcid in Currencies.AcceptedFor(kind).Keys)
        {
            foreach (var item in player.GetInventoryItemsOfWCID(wcid))
            {
                var charges = item.Structure ?? 0;

                if (charges > 0)
                    held.Add((item, charges));
            }
        }

        if (held.Count == 0)
            return new(false, $"You are not carrying any {label}.");

        if (amount == All)
            amount = held.Sum(h => (long)h.Charges);

        long collected = 0;

        foreach (var (item, charges) in held.OrderBy(h => h.Charges))
        {
            if (collected + charges > amount)
                continue;

            if (player.TryConsumeFromInventoryWithNetworking(item, 1))
                collected += charges;
        }

        if (collected == 0)
        {
            var smallest = held.Min(h => h.Charges);

            return new(false,
                $"Your smallest key carries {smallest} uses, more than the {amount} you asked to bank. " +
                "Keys cannot be split.");
        }

        if (!BankDb.TryAdjust(player.Account.AccountId, kind, collected, out var balance))
        {
            GiveOut(player, kind, collected);
            return new(false, "The bank could not record that deposit. Your keys were returned.");
        }

        var note = collected < amount ? $" (whole keys only, so {collected} of {amount})" : "";
        return new(true, $"Deposited {collected:N0} {label} uses{note}. Balance: {balance:N0}.");
    }

    // --------------------------------------------------------------- withdraw

    public static BankResult Withdraw(Player player, CurrencyKind kind, long amount)
    {
        if (amount == 0 || (amount < 0 && amount != All))
            return new(false, "Amount must be a positive number, or 'all'.");

        // Nothing to withdraw INTO - there is no radiance item, by design. Spending it
        // is what a shop is for; carrying it is not a thing.
        if (Currencies.IsEarned(kind))
            return new(false, $"{Currencies.DisplayName(kind)} cannot be carried, only spent. " +
                              "It stays in your account balance.");

        var label = Currencies.DisplayName(kind).ToLowerInvariant();
        var accountId = player.Account.AccountId;
        var balance = BankDb.GetBalance(accountId, kind);

        if (amount == All)
            amount = balance;

        if (amount <= 0)
            return new(false, $"You have no {label} banked.");

        if (balance < amount)
            return new(false, $"You only have {balance:N0} {label} banked.");

        // Luminance is hard-capped at MaximumLuminance and AddLuminance silently
        // bins the overflow, so clamp here rather than losing the difference.
        if (kind == CurrencyKind.Luminance)
        {
            if (player.MaximumLuminance is null or 0)
                return new(false, "You are not attuned to luminance yet, so you cannot withdraw it.");

            var headroom = (player.MaximumLuminance ?? 0) - (player.AvailableLuminance ?? 0);

            if (headroom <= 0)
                return new(false, $"You are already at your luminance maximum of {player.MaximumLuminance:N0}.");

            if (amount > headroom)
                return new(false, $"You can only hold {headroom:N0} more luminance right now.");
        }

        if (!BankDb.TryAdjust(accountId, kind, -amount, out var remaining))
            return new(false, "The bank could not complete that withdrawal.");

        var delivered = GiveOut(player, kind, amount);

        if (delivered < amount)
        {
            BankDb.TryAdjust(accountId, kind, amount - delivered, out remaining);

            if (delivered == 0)
                return new(false, "You have no room for that. Nothing was withdrawn.");

            return new(true,
                $"Withdrew {delivered:N0}{Currencies.UnitWord(kind)} of {label} - your pack filled up. " +
                $"Balance: {remaining:N0}.");
        }

        return new(true, $"Withdrew {amount:N0}{Currencies.UnitWord(kind)} of {label}. Balance: {remaining:N0}.");
    }

    /// <summary>
    /// Hands over an amount, largest denomination first, and reports how much
    /// actually fit in the pack.
    /// </summary>
    private static long GiveOut(Player player, CurrencyKind kind, long amount)
    {
        if (kind == CurrencyKind.Luminance)
        {
            player.GrantLuminance(amount, XpType.Admin, ShareType.None);
            return amount;
        }

        long delivered = 0;

        foreach (var denom in Currencies.PayoutFor(kind))
        {
            while (amount - delivered >= denom.Unit)
            {
                var wanted = (amount - delivered) / denom.Unit;
                var count = denom.MaxStack > 1 ? (int)Math.Min(wanted, denom.MaxStack) : 1;

                var item = WorldObjectFactory.CreateNewWorldObject(denom.Wcid);

                if (item is null)
                    break;

                if (denom.MaxStack > 1)
                    item.SetStackSize(count);

                if (!player.TryCreateInInventoryWithNetworking(item))
                {
                    item.Destroy();
                    return delivered;
                }

                delivered += denom.Unit * count;
            }
        }

        return delivered;
    }

    // ----------------------------------------------------------------- report

    public static long CarriedPyreals(Player player)
    {
        long total = 0;

        foreach (var denom in Currencies.PyrealDenominations)
        {
            foreach (var stack in player.GetInventoryItemsOfWCID(denom.Wcid))
                total += denom.Unit * (stack.StackSize ?? 1);
        }

        return total;
    }

    public static string Statement(Player player)
    {
        var accountId = player.Account.AccountId;

        var balances = BankDb.GetAllBalances(accountId);

        // Earnings sit in memory for up to Settings.FlushSeconds before they are
        // written. Folding them in here is what stops a player killing something,
        // typing /bank, and seeing none of it.
        foreach (var (kind, pending) in Earning.PendingFor(accountId))
            balances[kind] = balances.GetValueOrDefault(kind) + pending;

        var sb = new StringBuilder("Bank balance (shared across your account):\n");

        foreach (var kind in Enum.GetValues<CurrencyKind>())
            sb.AppendLine($"  {Currencies.DisplayName(kind),-18} {balances[kind],18:N0}{Currencies.UnitWord(kind)}");

        return sb.ToString();
    }

    /// <summary>
    /// A single balance, including anything earned but not yet flushed.
    ///
    /// Every read that a player can act on goes through here rather than BankDb
    /// directly, so a transfer cannot refuse to send currency the statement just
    /// said they had.
    /// </summary>
    public static long BalanceOf(uint accountId, CurrencyKind kind) =>
        BankDb.GetBalance(accountId, kind) + Earning.PendingFor(accountId, kind);
}
