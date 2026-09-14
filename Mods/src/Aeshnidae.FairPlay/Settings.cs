namespace Aeshnidae.FairPlay;

/// <summary>
/// Where the mule is sent back to. Defaults to the destination of ACE's own
/// "portalmarketplace" weenie (wcid 23032), read out of the world database, so the
/// landing spot is exactly where the normal Marketplace portal drops you.
/// </summary>
public class MarketplaceSettings
{
    /// <summary>Landblock that counts as "inside the Marketplace". 0x016C.</summary>
    public string Landblock { get; set; } = "016C";

    /// <summary>Cell to land in when bounced back.</summary>
    public string Cell { get; set; } = "016C01BC";

    public float X { get; set; } = 49.206f;
    public float Y { get; set; } = -31.935f;
    public float Z { get; set; } = 0.005f;

    public float RotationX { get; set; } = 0f;
    public float RotationY { get; set; } = 0f;
    public float RotationZ { get; set; } = -0.707f;
    public float RotationW { get; set; } = 0.7071f;

    public string BounceMessage { get; set; } =
        "Only one of your characters may be outside the Marketplace at a time.";

    public ushort LandblockId =>
        ushort.TryParse(Landblock, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : (ushort)0x016C;

    public uint CellId =>
        uint.TryParse(Cell, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0x016C01BCu;
}

public class Settings
{
    public const string FileName = "Settings.json";

    public bool Enabled { get; set; } = true;

    // ---------------------------------------------------------------- characters

    /// <summary>
    /// How many characters may be *logged in at once* from one address.
    ///
    /// This is the real limit: players may create as many characters and hold as many
    /// accounts as they like. What is rationed is simultaneous presence. The third
    /// character to enter the world from an address is logged straight back out.
    /// </summary>
    public int MaxConcurrentPerAddress { get; set; } = 2;

    public string TooManyOnlineMessage { get; set; } =
        "Only {0} of your characters may be logged in at once.";

    /// <summary>
    /// ACE's own creation cap, applied at startup. 11 is ACE's default and means "no
    /// meaningful limit" - characters are not what we are rationing. Set it lower only
    /// if you actually want to stop people *making* characters.
    /// </summary>
    public int MaxCharsPerAccount { get; set; } = 11;

    /// <summary>
    /// Accounts at or above this access level are exempt from the *limits* - the
    /// number of characters they may have online at once.
    ///
    /// They are NOT exempt from anything that records or flags. Staff are exactly who
    /// an audit trail exists to cover, and an audit with a hole shaped like the people
    /// holding the most power is not much of an audit.
    /// </summary>
    public string ExemptAtOrAbove { get; set; } = nameof(AccessLevel.Developer);

    /// <summary>
    /// Whether staff also skip the Marketplace rule.
    ///
    /// False means an admin running two clients gets one of them sent to the
    /// Marketplace like anybody else. Set true if that gets in the way of running the
    /// server - it is a limit rather than an audit, so exempting staff here costs no
    /// visibility.
    /// </summary>
    public bool MarketplaceExemptsStaff { get; set; } = false;

    /// <summary>
    /// Accounts that may be created from one address. Auto-creation is on, so without
    /// a cap anyone reaching the login port can mint accounts without limit - which is
    /// both a spam vector and a way to pollute the household graph with noise.
    /// 0 disables the cap.
    /// </summary>
    public int MaxAccountsPerAddress { get; set; } = 3;

    // -------------------------------------------------------------- marketplace

    /// <summary>
    /// At most one character per IP address may be outside the Marketplace. Whoever is
    /// already out keeps their place; the one who tries to join them is sent back.
    /// Nothing is stored - the rule is evaluated live from who is online and where
    /// they are, so swapping is automatic.
    ///
    /// Keyed on address, not account: the limit is meant to be one person at a time,
    /// and a second account on the same machine is the same person.
    /// </summary>
    public bool OneCharacterOutsideMarketplace { get; set; } = true;

    /// <summary>
    /// Addresses the Marketplace rule ignores.
    ///
    /// An address-based rule cannot tell two people sharing a house from one person
    /// running two clients. This is the escape hatch for the former - siblings, a
    /// shared flat, a LAN party. Add their address and both play normally.
    /// </summary>
    public string[] ExemptAddresses { get; set; } = Array.Empty<string>();

    public MarketplaceSettings Marketplace { get; set; } = new();

    /// <summary>
    /// How often to sweep everyone online, in seconds. 0 disables the sweep.
    ///
    /// The event hooks (teleport, login) only fire when someone moves, so a character
    /// already standing outside the Marketplace when this mod loads - or who got out
    /// by a route with no hook - would never be noticed. The sweep is what makes the
    /// rule true continuously rather than only at the moment of travel.
    /// </summary>
    public double SweepSeconds { get; set; } = 15.0;

    /// <summary>
    /// When the sweep finds two of an account's characters already outside, which one
    /// keeps its place. "NewestLogin" sends the character that has been logged in
    /// longest back to the Marketplace; "OldestLogin" does the reverse.
    ///
    /// This only decides the already-broken case. When someone actively travels out,
    /// the traveller is the one bounced, because that is the character that just tried
    /// to break the rule.
    /// </summary>
    public string KeepOutside { get; set; } = "NewestLogin";

    // --------------------------------------------------------------- allegiance

    /// <summary>
    /// Refuse an allegiance pledge when patron and vassal are connected from the
    /// same IP address - the usual shape of someone swearing their own alt to
    /// themselves to farm passup XP.
    /// </summary>
    public bool BlockSameIpAllegiance { get; set; } = true;

    public string AllegianceBlockedMessage { get; set; } =
        "You cannot swear allegiance to a character connected from your own address.";

    // ---------------------------------------------------------------------- IPs

    /// <summary>
    /// Treat accounts that have ever shared an address as one household, and enforce
    /// against the household rather than the live address.
    ///
    /// Without this, every rule here is defeated by a VPN: a second client on a
    /// different address simply is not seen as the same person. History does not
    /// evaporate, so once two accounts are observed together they stay linked.
    ///
    /// Links are built only from addresses NOT in ExemptAddresses - otherwise a
    /// genuinely shared connection would weld strangers together permanently.
    /// </summary>
    public bool LinkAccountsBySharedAddress { get; set; } = true;

    /// <summary>
    /// Accounts that never join a household, whatever they share an address with.
    /// The escape hatch for someone caught by an internet cafe, student halls, or a
    /// one-off login from a friend's house.
    /// </summary>
    public string[] NeverLink { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Announce when one character hands an item to another in the same household.
    /// A flag, not a block - moving your own gear to your own mule is normal; doing it
    /// fourteen times in an evening is the pattern worth seeing.
    /// </summary>
    public bool FlagLinkedTrades { get; set; } = true;

    /// <summary>
    /// Flag when an item one character drops is picked up by a different character.
    ///
    /// Dropping on the ground is how you move an item without a trade, which makes it
    /// the obvious way around trade logging and the linked-trade flag. A drop on its
    /// own is noise - people discard junk constantly - so what is recorded is the
    /// *handover*: dropped by one person, collected by another.
    /// </summary>
    public bool FlagGroundTransfers { get; set; } = true;

    /// <summary>
    /// How long after a drop a pickup still counts as a handover, in seconds. Long
    /// enough to cover walking a mule over; short enough that genuinely abandoned
    /// loot someone stumbles on an hour later is not reported.
    /// </summary>
    public double GroundTransferWindowSeconds { get; set; } = 600;

    /// <summary>
    /// Flag every drop, not just the ones someone else collects. Very noisy - off by
    /// default - but useful for a short window when investigating one player.
    /// </summary>
    public bool FlagAllDrops { get; set; } = false;

    /// <summary>
    /// Flag when an item one character sells to a vendor is bought by a different
    /// character.
    ///
    /// The third dead drop, after the ground and a housing chest: ACE keeps a
    /// player-sold item on the vendor until somebody buys it or it rots, so selling it
    /// and having the other character buy it back moves it with no trade, give or drop
    /// to see. Ordinary selling is never reported - only the completed handover.
    /// </summary>
    public bool FlagVendorHandovers { get; set; } = true;

    /// <summary>
    /// How long after a sale a purchase by someone else still counts as a handover, in
    /// seconds. Longer than the ground window because the item is in no danger on the
    /// vendor, so there is no hurry to collect it.
    /// </summary>
    public double VendorHandoverWindowSeconds { get; set; } = 1800;

    /// <summary>
    /// Match logins against downloaded VPN / datacentre CIDR lists.
    ///
    /// Not used to block. Blocking VPNs punishes the legitimate - corporate networks,
    /// countries where a VPN is simply how people use the internet, the merely
    /// privacy-minded - while an adversary just rents a residential proxy instead.
    /// What it is good for is the two things below.
    /// </summary>
    public bool UseVpnLists { get; set; } = true;

    /// <summary>Folder of one-CIDR-per-line .txt files. Empty means &lt;mod folder&gt;pnlists.</summary>
    public string VpnListDirectory { get; set; } = "";

    /// <summary>Announce logins arriving from a listed VPN or datacentre range.</summary>
    public bool FlagVpnLogins { get; set; } = true;

    /// <summary>
    /// Do not let a VPN address link accounts together, or count as evidence of a
    /// separate identity.
    ///
    /// This is the useful half. Two strangers who happen to share an exit node are not
    /// a household, and someone switching to a VPN should not thereby appear to be a
    /// new person. Excluding these addresses from the graph makes the linking both
    /// fairer and harder to game.
    /// </summary>
    public bool ExcludeVpnFromLinking { get; set; } = true;

    /// <summary>Record the source address of every login, for correlation.</summary>
    public bool RecordLoginAddresses { get; set; } = true;

    /// <summary>Announce when one address is seen using more than this many accounts.</summary>
    public int FlagAccountsPerAddress { get; set; } = 3;

    /// <summary>Announce when one account is seen from more than this many addresses.</summary>
    public int FlagAddressesPerAccount { get; set; } = 5;

    /// <summary>Days of login history to keep. 0 keeps everything.</summary>
    public int HistoryDays { get; set; } = 90;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public AccessLevel ExemptLevel =>
        Enum.TryParse<AccessLevel>(ExemptAtOrAbove, true, out var lvl) ? lvl : AccessLevel.Developer;

    /// <summary>Accounts pinned to a household of one.</summary>
    public bool IsNeverLinked(string? account) =>
        !string.IsNullOrEmpty(account) &&
        (NeverLink ?? Array.Empty<string>()).Any(a => string.Equals(a, account, StringComparison.OrdinalIgnoreCase));

    /// <summary>Addresses that genuinely host more than one person.</summary>
    public bool IsAddressExempt(string? address) =>
        !string.IsNullOrEmpty(address) &&
        (ExemptAddresses ?? Array.Empty<string>()).Any(a => string.Equals(a, address, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Staff are outside the *limits* only. Deliberately not called by anything that
    /// records or flags - see ExemptAtOrAbove.
    /// </summary>
    public bool IsLimitExempt(Player? player) =>
        player?.Session is { } s && s.AccessLevel >= ExemptLevel;

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
                loaded.Marketplace ??= new MarketplaceSettings();
                loaded.ExemptAddresses ??= Array.Empty<string>();
                loaded.NeverLink ??= Array.Empty<string>();
                return loaded;
            }

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path}");
            return defaults;
        }
        catch (Exception ex)
        {
            // Defaults enforce MORE than a broken file would. For a fairness rule the
            // safe direction is on, not off.
            ModManager.Log($"[{Mod.Name}] could not read {path}, using defaults: {ex.Message}", ModManager.LogLevel.Warn);
            return new Settings();
        }
    }

    public void Save(string modPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(modPath, FileName), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not save settings: {ex.Message}", ModManager.LogLevel.Error);
        }
    }
}
