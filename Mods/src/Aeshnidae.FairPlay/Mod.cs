namespace Aeshnidae.FairPlay;

/// <summary>
/// Entry point ACE looks for: assembly "Aeshnidae.FairPlay.dll" -> type
/// "Aeshnidae.FairPlay.Mod".
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";

    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    public static Settings Settings { get; private set; } = new();

    internal static IpRegistry? Logins { get; private set; }

    internal static VpnList? Vpn { get; private set; }

    /// <summary>Counters for /fairplay, so the rules are visible rather than mysterious.</summary>
    public static long Bounces, PledgesBlocked, LoginsRecorded, LogoutsForced, LinkedTrades, GroundTransfers, VendorHandovers, VpnLogins, AccountsRefused;

    private static Harmony? _harmony;
    private static System.Timers.Timer? _sweep;
    private bool _disposed;

    public void Initialize()
    {
        try
        {
            Settings = Settings.Load(ModPath);

            // Load the VPN lists first: IpRegistry consults them while building the
            // household graph, so an empty list there would link on VPN addresses.
            if (Settings.UseVpnLists)
            {
                Vpn = VpnList.Load(Path.Combine(ModPath, "vpnlists"));
                ModManager.Log($"[{Name}] VPN lists: {Vpn.Ranges:N0} ranges from {Vpn.Source}");
            }

            if (Settings.RecordLoginAddresses)
                Logins = new IpRegistry(ModPath, Settings);

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

            // One source of truth for the number: ACE's own property. The mod owns the
            // exemption, not the count, so the two can never disagree.
            if (Settings.MaxCharsPerAccount > 0)
            {
                var current = PropertyManager.GetLong("max_chars_per_account").Item;

                if (current != Settings.MaxCharsPerAccount)
                {
                    PropertyManager.ModifyLong("max_chars_per_account", Settings.MaxCharsPerAccount);
                    ModManager.Log($"[{Name}] max_chars_per_account {current} -> {Settings.MaxCharsPerAccount}");
                }
            }

            StartSweep();

            ModManager.Log($"[{Name}] active - max {Settings.MaxConcurrentPerAddress} online per address " +
                           $"(exempt {Settings.ExemptLevel}+), " +
                           $"marketplace rule {(Settings.OneCharacterOutsideMarketplace ? "on" : "off")}, " +
                           $"same-IP allegiance {(Settings.BlockSameIpAllegiance ? "blocked" : "allowed")}, " +
                           $"login history {Logins?.KnownAccounts ?? 0} account(s) / {Logins?.KnownAddresses ?? 0} address(es)");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] failed to initialize: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Runs the Marketplace sweep on a timer. The patches only fire when someone
    /// travels, which cannot catch a character already standing outside when the mod
    /// loaded - the first version of this mod missed exactly that case.
    /// </summary>
    private static void StartSweep()
    {
        _sweep?.Stop();
        _sweep?.Dispose();
        _sweep = null;

        if (Settings.SweepSeconds <= 0)
            return;

        _sweep = new System.Timers.Timer(Math.Max(5.0, Settings.SweepSeconds) * 1000) { AutoReset = true };
        _sweep.Elapsed += (_, _) => MarketplaceRule.Sweep();
        _sweep.Start();
    }

    /// <summary>The address a player is connected from, or null if it cannot be determined.</summary>
    internal static string? AddressOf(Player? player)
    {
        try { return player?.Session?.EndPointC2S?.Address?.ToString(); }
        catch { return null; }
    }

    /// <summary>
    /// The key the fair-play rules group on: the household an account belongs to when
    /// linking is on and we know the account, otherwise the live address.
    ///
    /// This is what makes the rules survive a VPN - the household is derived from
    /// history, not from where the player happens to be connecting from tonight.
    /// </summary>
    internal static string? IdentityOf(Player? player)
    {
        try
        {
            var account = player?.Account?.AccountName;

            if (!string.IsNullOrEmpty(account) && Settings.LinkAccountsBySharedAddress && Logins is not null)
            {
                var household = Logins.HouseholdOf(account);

                if (!string.IsNullOrEmpty(household))
                    return "household:" + household;
            }

            var address = AddressOf(player);
            return string.IsNullOrEmpty(address) ? null : "ip:" + address;
        }
        catch { return null; }
    }

    /// <summary>
    /// How many accounts already exist that were created from this address.
    /// ACE stores CreateIP as the raw address bytes, so the comparison is on bytes.
    /// </summary>
    internal static int CountAccountsCreatedFrom(IPAddress address)
    {
        try
        {
            var wanted = address.GetAddressBytes();

            using var ctx = new ACE.Database.Models.Auth.AuthDbContext();

            return ctx.Account
                .AsEnumerable()
                .Count(a => a.CreateIP is not null && a.CreateIP.SequenceEqual(wanted));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] could not count accounts for {address}: {ex.Message}",
                           ModManager.LogLevel.Warn);
            return 0;   // fail open: never block creation because the count failed
        }
    }

    /// <summary>Is this address in a known VPN or datacentre range?</summary>
    internal static bool IsVpnAddress(string? address)
    {
        try { return Settings.UseVpnLists && (Vpn?.Contains(address) ?? false); }
        catch { return false; }
    }

    /// <summary>Records a login and announces any correlation worth a look.</summary>
    internal static void RecordLogin(Player? player)
    {
        try
        {
            if (Logins is null || player?.Session is null)
                return;

            var account = player.Account?.AccountName;
            var address = AddressOf(player);

            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(address))
                return;

            Interlocked.Increment(ref LoginsRecorded);

            if (Settings.FlagVpnLogins && IsVpnAddress(address))
            {
                Interlocked.Increment(ref VpnLogins);
                PlayerManager.BroadcastToAuditChannel(null,
                    $"[FairPlay] {account} ({player.Name}) logged in from {address}, a known VPN/datacentre range");
            }

            foreach (var note in Logins.Record(account, player.Name, address))
                PlayerManager.BroadcastToAuditChannel(null, $"[FairPlay] {note}");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] could not record login for {player?.Name}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _sweep?.Stop();
            _sweep?.Dispose();
            _sweep = null;

            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            Logins?.Dispose();
            Logins = null;
            Vpn = null;

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
