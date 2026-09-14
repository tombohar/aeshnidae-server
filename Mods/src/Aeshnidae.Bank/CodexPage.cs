namespace Aeshnidae.Bank;

/// <summary>
/// The Bank's page in the Aeshnidae Codex. Found by Aeshnidae.Codex through reflection
/// (a static class called CodexPage with Title and Pages(Player)), so nothing here
/// references that mod. Plain text: the book font is proportional, so nothing is
/// column-aligned on purpose.
/// </summary>
public static class CodexPage
{
    public static string Title => "Bank";

    public static string[] Pages(Player player)
    {
        if (player?.Account is null || !BankDb.Ready)
            return Array.Empty<string>();

        var accountId = player.Account.AccountId;
        var balances = BankDb.GetAllBalances(accountId);

        foreach (var (kind, pending) in Earning.PendingFor(accountId))
            balances[kind] = balances.GetValueOrDefault(kind) + pending;

        var sb = new StringBuilder();
        sb.AppendLine("THE BANK");
        sb.AppendLine();
        sb.AppendLine("Shared across every character on this account.");
        sb.AppendLine();

        foreach (var kind in Enum.GetValues<CurrencyKind>())
            sb.AppendLine($"{Currencies.DisplayName(kind)}: {balances[kind]:N0}{Currencies.UnitWord(kind)}");

        var length = History.SessionLength(player);

        if (length > TimeSpan.Zero)
        {
            sb.AppendLine();
            sb.AppendLine($"This session ({(int)length.TotalHours}h {length.Minutes:00}m):");

            foreach (var kind in new[] { CurrencyKind.Radiance, CurrencyKind.Resonance, CurrencyKind.Luminance })
            {
                var earned = History.Session(player, kind);

                if (earned > 0)
                    sb.AppendLine($"  +{earned:N0} {Currencies.DisplayName(kind)}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("/b d deposits everything you carry that the bank takes.");
        sb.AppendLine("/b w <what> <n> withdraws. /b pay <name> rad <n> sends Radiance.");
        sb.AppendLine("Radiance buys skill mastery; Resonance will buy auras.");

        return new[] { sb.ToString().TrimEnd() };
    }
}
