namespace Aeshnidae.Bank;

/// <summary>
/// Moves radiance and resonance between accounts.
///
/// Restricted to the earned currencies on purpose. Pyreals, luminance and keys all have
/// a carried form and therefore already have a way to change hands - trade windows, and
/// the game's own economy around them. Radiance and resonance have no carried form at
/// all, so without this they could only ever move one way, out of the world and into a
/// balance, and an economy needs them to circulate.
/// </summary>
public static class Transfer
{
    /// <summary>
    /// Serialises the whole check-then-move sequence.
    ///
    /// Both halves are read-modify-writes against the same table, and BankDb's own
    /// per-row guard stops a balance going negative but says nothing about two transfers
    /// interleaving. This does: no transfer is ever half-applied, because nothing is
    /// written until every check has passed.
    /// </summary>
    private static readonly object _gate = new();

    public static BankResult Send(Player sender, string recipientName, CurrencyKind kind, long amount)
    {
        if (!Mod.Settings.AllowPlayerTransfers)
            return new(false, "Transfers are switched off on this server.");

        if (!Currencies.IsEarned(kind))
            return new(false, $"{Currencies.DisplayName(kind)} cannot be sent this way. " +
                              "Only Radiance and Resonance move between accounts - trade the rest in person.");

        if (amount < Mod.Settings.MinimumTransfer)
            return new(false, $"The smallest transfer is {Mod.Settings.MinimumTransfer:N0}.");

        if (string.IsNullOrWhiteSpace(recipientName))
            return new(false, "Send it to whom?");

        // Finds offline characters too, which is the point - you should not have to
        // catch somebody online to pay them.
        var recipient = PlayerManager.FindByName(recipientName.Trim());

        if (recipient?.Account is null)
            return new(false, $"No character called '{recipientName.Trim()}'.");

        var senderAccount = sender.Account.AccountId;
        var recipientAccount = recipient.Account.AccountId;

        if (senderAccount == recipientAccount)
            return new(false, $"{recipient.Name} is on your own account - the balance is already shared.");

        lock (_gate)
        {
            // Make the database authoritative before reading a balance to spend from.
            // Without this a sender whose recent kills are still buffered would be told
            // they have currency by /bank and refused by the debit below, which reads
            // the written balance only.
            Earning.Flush();

            var balance = BankDb.GetBalance(senderAccount, kind);

            if (balance < amount)
                return new(false, $"You have {balance:N0} {Currencies.DisplayName(kind)}, " +
                                  $"which is not {amount:N0}.");

            var tax = (long)Math.Round(amount * Math.Clamp(Mod.Settings.TransferTax, 0, 1));
            var delivered = amount - tax;

            if (delivered <= 0)
                return new(false, "The whole transfer would be lost to the transit fee. Send more.");

            if (!BankDb.TryAdjust(senderAccount, kind, -amount, out _))
                return new(false, "The transfer was refused - your balance changed while it was being sent.");

            if (!BankDb.TryAdjust(recipientAccount, kind, delivered, out _))
            {
                // Put it back. A failed credit that kept the debit is the one outcome
                // that destroys currency and cannot be explained to the player.
                if (!BankDb.TryAdjust(senderAccount, kind, amount, out _))
                    ModManager.Log($"[{Mod.Name}] TRANSFER LOST: {amount:N0} {kind} debited from account " +
                                   $"{senderAccount} for {recipient.Name}, credit failed, refund ALSO failed. " +
                                   "This balance needs fixing by hand.", ModManager.LogLevel.Error);

                return new(false, "The transfer failed and your balance has been restored.");
            }

            ModManager.Log($"[{Mod.Name}] {sender.Name} sent {delivered:N0} {kind} to {recipient.Name} " +
                           $"(gross {amount:N0}, fee {tax:N0})");

            // Told immediately if they are online, because a currency arriving silently
            // is indistinguishable from one that never arrived.
            var online = PlayerManager.GetOnlinePlayer(recipient.Guid);

            online?.SendMessage($"{sender.Name} sent you {delivered:N0} {Currencies.DisplayName(kind)}.");
            Hud.Refresh(online);

            var note = tax > 0 ? $" ({tax:N0} lost to the transit fee)" : "";

            return new(true, $"Sent {delivered:N0} {Currencies.DisplayName(kind)} to {recipient.Name}{note}.");
        }
    }
}
