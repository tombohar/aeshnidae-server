namespace Aeshnidae.Bank;

/// <summary>
/// The bank panel for the HUD feed: what /bank prints, as a window.
///
/// The feed itself - the /hud switch, who is listening, the wire contract - is
/// Aeshnidae.Hud; this mod is one provider. HudFeed.cs is the verbatim copy of that
/// mod's Feed.cs (cross-mod types are unsafe, ACE's are not), and /hud-bank is the
/// command the Hud mod invokes when a session turns the feed on or asks for a sync.
///
/// Every button is a /b command the player could have typed, with the selected row's
/// short name and the two text boxes filled in. So the panel adds nothing the bank
/// does not already do; it just puts the balances where the player can see them and
/// saves the typing. Nothing here touches a balance.
/// </summary>
public static class Hud
{
    /// <summary>Re-send the bank panel, if the session is listening. Safe to call from anywhere.</summary>
    public static void Refresh(Player? player)
    {
        try
        {
            if (player?.Session is null || player.Account is null || !BankDb.Ready || !HudFeed.IsOn(player))
                return;

            HudFeed.Send(player.Session, BankPanel(player));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] hud refresh failed: {ex}", ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// One row per currency: banked (including earnings not yet flushed, exactly as the
    /// statement does), and carried, so "is there anything to deposit" is visible
    /// without opening the pack. Rows with something carried are coloured. The earned
    /// currencies have no carried form and say so.
    /// </summary>
    public static object BankPanel(Player player)
    {
        var accountId = player.Account.AccountId;
        var balances = BankDb.GetAllBalances(accountId);

        foreach (var (kind, pending) in Earning.PendingFor(accountId))
            balances[kind] = balances.GetValueOrDefault(kind) + pending;

        var rows = new List<object>();

        foreach (var kind in Enum.GetValues<CurrencyKind>())
        {
            var carried = Carried(player, kind);
            var unit = Currencies.UnitWord(kind).Trim();

            rows.Add(new
            {
                k = ShortName(kind),
                c = new[]
                {
                    Currencies.DisplayName(kind),
                    Format(balances.GetValueOrDefault(kind), unit),
                    carried is null ? "-" : Format(carried.Value, unit),
                },
                col = carried > 0 ? "#9BE39B" : null,
            });
        }

        return new
        {
            v = 1,
            id = "bank",
            title = "Aeshnidae - Bank",
            sub = "Shared across your account. Pick a row; a blank amount means all of it.",
            cols = new object[]
            {
                new { n = "Currency", w = 130 },
                new { n = "Banked", w = 120 },
                new { n = "Carried", w = 0 },
            },
            rows,
            flds = new object[]
            {
                new { k = "amount", l = "Amount", w = 80 },
                new { k = "to", l = "Pay to", w = 130 },
            },
            acts = new object[]
            {
                new { l = "Deposit", c = "/b d {key} {amount}", row = true },
                new { l = "Withdraw", c = "/b w {key} {amount}", row = true },
                new { l = "Deposit all", c = "/b d" },
                new { l = "Pay", c = "/b pay {to} {key} {amount}", row = true },
                new { l = "Refresh", c = "/hud-bank" },
            },
        };
    }

    /// <summary>The provider command Aeshnidae.Hud invokes; a player can also type it.</summary>
    [CommandHandler("hud-bank", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Re-send the bank panel to a client showing the HUD feed.",
        "/hud-bank")]
    public static void HandleHudBank(Session session, params string[] parameters) =>
        Refresh(session.Player);

    /// <summary>
    /// What the player is carrying of a currency, in the bank's unit (pyreals, points,
    /// key uses); null for the earned currencies, which have no carried form.
    /// </summary>
    private static long? Carried(Player player, CurrencyKind kind)
    {
        switch (kind)
        {
            case CurrencyKind.Pyreal:
                return BankService.CarriedPyreals(player);

            case CurrencyKind.Luminance:
                return player.AvailableLuminance ?? 0;

            case CurrencyKind.LegendaryKey:
            case CurrencyKind.SturdyIronKey:
                long uses = 0;

                foreach (var wcid in Currencies.AcceptedFor(kind).Keys)
                    foreach (var item in player.GetInventoryItemsOfWCID(wcid))
                        uses += item.Structure ?? 0;

                return uses;

            default:
                return null;
        }
    }

    /// <summary>The row key: the alias /b already accepts, so the command reads as typed.</summary>
    private static string ShortName(CurrencyKind kind) => kind switch
    {
        CurrencyKind.Pyreal => "p",
        CurrencyKind.Luminance => "l",
        CurrencyKind.LegendaryKey => "lk",
        CurrencyKind.SturdyIronKey => "sik",
        CurrencyKind.Radiance => "rad",
        CurrencyKind.Resonance => "res",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string Format(long amount, string unit) =>
        unit.Length == 0 ? amount.ToString("N0") : $"{amount:N0} {unit}";
}
