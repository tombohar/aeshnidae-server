namespace Aeshnidae.Bank;

/// <summary>What a bank balance is denominated in.</summary>
public enum CurrencyKind
{
    Pyreal,
    Luminance,
    LegendaryKey,
    SturdyIronKey,

    /// <summary>Earned from monster kills. See Earning.</summary>
    Radiance,

    /// <summary>Earned from completing quests. See Earning.</summary>
    Resonance,
}

/// <summary>
/// One physical item a balance can be paid out in.
///
/// For stackable currency (pyreals, trade notes) <see cref="Unit"/> is the face
/// value and several fit in one stack. For keys it is the number of charges the
/// key carries in PropertyInt.Structure - keys do not stack, so each payout is a
/// separate item.
/// </summary>
/// <param name="Wcid">Weenie to create.</param>
/// <param name="Unit">Value (pyreals) or charges (keys) this item is worth.</param>
/// <param name="MaxStack">Max stack size, 1 for non-stacking items.</param>
/// <param name="Name">Display name, for messages.</param>
public record Denomination(uint Wcid, long Unit, int MaxStack, string Name);

public static class Currencies
{
    /// <summary>
    /// Trade notes and pyreal coin, largest first. Values and stack limits read
    /// out of the world database rather than assumed.
    /// </summary>
    public static readonly Denomination[] PyrealDenominations =
    {
        new(20630, 250_000, 250, "Trade Note (250,000)"),
        new(20629, 200_000, 250, "Trade Note (200,000)"),
        new(20628, 150_000, 250, "Trade Note (150,000)"),
        new( 2627, 100_000, 250, "Trade Note (100,000)"),
        new( 7377,  75_000, 250, "Trade Note (75,000)"),
        new( 2626,  50_000, 250, "Trade Note (50,000)"),
        new( 7376,  25_000, 250, "Trade Note (25,000)"),
        new( 7375,  20_000, 250, "Trade Note (20,000)"),
        new( 7374,  15_000, 250, "Trade Note (15,000)"),
        new( 2625,  10_000, 250, "Trade Note (10,000)"),
        new( 2624,   5_000, 250, "Trade Note (5,000)"),
        new( 2623,   1_000, 250, "Trade Note (1,000)"),
        new( 2622,     500, 250, "Trade Note (500)"),
        new( 2621,     100, 250, "Trade Note (100)"),
        new(  273,       1, 25_000, "Pyreal"),
    };

    /// <summary>
    /// Every Legendary Key variant in the world database, by charge count.
    /// 48914 and 51558 are the only two that appear in loot; the rest are quest
    /// and vendor variants, all mechanically interchangeable.
    /// </summary>
    public static readonly Denomination[] LegendaryKeyDenominations =
    {
        new(51963, 25, 1, "Legendary Key (25 uses)"),
        new(51954, 10, 1, "Durable Legendary Key (10 uses)"),
        new(52010,  5, 1, "Legendary Key (5 uses)"),
        new(48750,  4, 1, "Legendary Key (4 uses)"),
        new(48749,  3, 1, "Legendary Key (3 uses)"),
        new(48748,  2, 1, "Legendary Key (2 uses)"),
        new(51558,  1, 1, "Legendary Key"),
    };

    /// <summary>Every wcid a Legendary Key deposit will accept, with its max charges.</summary>
    public static readonly Dictionary<uint, int> LegendaryKeyAccepted = new()
    {
        [48746] = 1,  [48747] = 1,  [48914] = 1,  [51558] = 1,
        [72048] = 1,  [72600] = 1,  [72628] = 1,  [72669] = 1,
        [48748] = 2,  [72474] = 2,  [72807] = 2,
        [48749] = 3,  [51586] = 3,  [51648] = 3,  [72338] = 3,  [72635] = 3,
        [48750] = 4,  [87168] = 4,
        [52010] = 5,
        [51954] = 10,
        [51963] = 25,
    };

    public static readonly Denomination[] SturdyIronKeyDenominations =
    {
        new(23194, 50, 1, "Sturdy Iron Keyring (50 uses)"),
        new( 6876,  1, 1, "Sturdy Iron Key"),
    };

    public static readonly Dictionary<uint, int> SturdyIronKeyAccepted = new()
    {
        [6876] = 1,
        [23194] = 50,
    };

    /// <summary>
    /// Long and short names. Multi-word names are matched after the command
    /// collapses the middle arguments, so "legendary key" and "lk" both land here.
    /// </summary>
    private static readonly Dictionary<string, CurrencyKind> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pyreal"] = CurrencyKind.Pyreal,
        ["pyreals"] = CurrencyKind.Pyreal,
        ["p"] = CurrencyKind.Pyreal,
        ["py"] = CurrencyKind.Pyreal,
        ["coin"] = CurrencyKind.Pyreal,
        ["cash"] = CurrencyKind.Pyreal,
        ["mmd"] = CurrencyKind.Pyreal,
        ["mmds"] = CurrencyKind.Pyreal,

        ["luminance"] = CurrencyKind.Luminance,
        ["lum"] = CurrencyKind.Luminance,
        ["l"] = CurrencyKind.Luminance,

        ["legendary key"] = CurrencyKind.LegendaryKey,
        ["legendary keys"] = CurrencyKind.LegendaryKey,
        ["legendarykey"] = CurrencyKind.LegendaryKey,
        ["legkey"] = CurrencyKind.LegendaryKey,
        ["legendary"] = CurrencyKind.LegendaryKey,
        ["lk"] = CurrencyKind.LegendaryKey,

        ["sturdy iron key"] = CurrencyKind.SturdyIronKey,
        ["sturdy iron keys"] = CurrencyKind.SturdyIronKey,
        ["sturdyironkey"] = CurrencyKind.SturdyIronKey,
        ["sturdy iron"] = CurrencyKind.SturdyIronKey,
        ["sturdy"] = CurrencyKind.SturdyIronKey,
        ["sik"] = CurrencyKind.SturdyIronKey,
        ["iron key"] = CurrencyKind.SturdyIronKey,
        ["ik"] = CurrencyKind.SturdyIronKey,

        ["radiance"] = CurrencyKind.Radiance,
        ["rad"] = CurrencyKind.Radiance,
        ["rd"] = CurrencyKind.Radiance,

        ["resonance"] = CurrencyKind.Resonance,
        ["res"] = CurrencyKind.Resonance,
        ["rs"] = CurrencyKind.Resonance,
    };

    /// <summary>
    /// Currencies with no physical form: earned into the account balance directly and
    /// never carried, so there is nothing to deposit from and nothing to withdraw into.
    ///
    /// This is the one real difference between them and every other balance here.
    /// Pyreals, luminance and keys all exist on your character and the bank moves them
    /// in and out; radiance and resonance only ever exist as a balance, which is what
    /// makes them account-wide by construction rather than by rule.
    /// </summary>
    public static bool IsEarned(CurrencyKind kind) =>
        kind is CurrencyKind.Radiance or CurrencyKind.Resonance;

    public static bool TryResolve(string input, out CurrencyKind kind) =>
        Aliases.TryGetValue((input ?? "").Trim(), out kind);

    public static string DisplayName(CurrencyKind kind) => kind switch
    {
        CurrencyKind.Pyreal => "Pyreals",
        CurrencyKind.Luminance => "Luminance",
        CurrencyKind.LegendaryKey => "Legendary Keys",
        CurrencyKind.SturdyIronKey => "Sturdy Iron Keys",
        CurrencyKind.Radiance => "Radiance",
        CurrencyKind.Resonance => "Resonance",
        _ => kind.ToString(),
    };

    /// <summary>"uses" for keys, "" for currency - keys are banked as charges.</summary>
    public static string UnitWord(CurrencyKind kind) =>
        kind is CurrencyKind.LegendaryKey or CurrencyKind.SturdyIronKey ? " uses" : "";

    /// <summary>
    /// Denominations a withdrawal may pay out, largest first. Key sizes are
    /// filtered by Settings so the server decides whether "withdraw 3" hands over
    /// one 3-use key or three singles.
    /// </summary>
    public static Denomination[] PayoutFor(CurrencyKind kind) => kind switch
    {
        CurrencyKind.Pyreal => PyrealDenominations,
        CurrencyKind.LegendaryKey => Filter(LegendaryKeyDenominations, Mod.Settings.LegendaryKeyPayout),
        CurrencyKind.SturdyIronKey => Filter(SturdyIronKeyDenominations, Mod.Settings.SturdyIronKeyPayout),
        _ => Array.Empty<Denomination>(),
    };

    private static Denomination[] Filter(Denomination[] all, int[] allowed)
    {
        // 1 must always survive, or a remainder could never be paid out.
        if (allowed is null || allowed.Length == 0)
            return all.Where(d => d.Unit == 1).ToArray();

        var set = allowed.Select(a => (long)a).ToHashSet();
        set.Add(1);

        return all.Where(d => set.Contains(d.Unit)).OrderByDescending(d => d.Unit).ToArray();
    }

    public static Dictionary<uint, int> AcceptedFor(CurrencyKind kind) => kind switch
    {
        CurrencyKind.LegendaryKey => LegendaryKeyAccepted,
        CurrencyKind.SturdyIronKey => SturdyIronKeyAccepted,
        _ => new Dictionary<uint, int>(),
    };
}
