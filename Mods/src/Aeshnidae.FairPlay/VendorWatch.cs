namespace Aeshnidae.FairPlay;

/// <summary>
/// Watches items sold to a vendor and who buys them back.
///
/// The third way to move an item without a trade, after the ground and a housing
/// chest: sell it to any vendor, walk the other character over, buy it. ACE keeps a
/// player-sold item on the vendor as a unique item until somebody buys it or it rots,
/// so it is a dead drop with a shopkeeper standing guard - no trade event, no give, no
/// drop, and it even looks like ordinary commerce in the vendor's own logs.
///
/// Same shape as GroundWatch, for the same reason: a sale on its own is nothing -
/// people sell loot all day - so what is reported is the completed handover, sold by
/// one character and bought by a different one inside a window. SAME HOUSEHOLD marks
/// the ones worth reading closely.
/// </summary>
internal static class VendorWatch
{
    private sealed record Sold(string Identity, string Character, string Item, string Vendor, DateTime When);

    /// <summary>item guid -> who sold it. Small and short-lived by construction.</summary>
    private static readonly ConcurrentDictionary<uint, Sold> _onVendor = new();

    private static DateTime _lastPrune = DateTime.UtcNow;

    /// <summary>An item that the vendor kept for resale, and who it came from.</summary>
    public static void OnSell(Player? player, WorldObject? item, string vendorName)
    {
        try
        {
            if (player is null || item is null || !Mod.Settings.Enabled || !Mod.Settings.FlagVendorHandovers)
                return;

            // No staff exemption - this is a record, not a restriction.
            var identity = Mod.IdentityOf(player);

            if (identity is null)
                return;

            var what = $"{item.Name} (wcid {item.WeenieClassId})";

            _onVendor[item.Guid.Full] = new Sold(identity, player.Name, what, vendorName, DateTime.UtcNow);

            Prune();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] vendor sell watch failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>A unique item leaving the vendor for a buyer.</summary>
    public static void OnBuy(Player? player, uint itemGuid)
    {
        try
        {
            if (player is null || !Mod.Settings.Enabled || !Mod.Settings.FlagVendorHandovers)
                return;

            // Only interesting if a player sold this exact item recently. Stock items
            // never reach here.
            if (!_onVendor.TryRemove(itemGuid, out var sale))
                return;

            var age = DateTime.UtcNow - sale.When;

            if (age.TotalSeconds > Math.Max(1, Mod.Settings.VendorHandoverWindowSeconds))
                return;

            // Buying your own item back is a change of mind, not a handover.
            if (string.Equals(player.Name, sale.Character, StringComparison.OrdinalIgnoreCase))
                return;

            var identity = Mod.IdentityOf(player);
            var sameHousehold = identity is not null && identity == sale.Identity;

            Interlocked.Increment(ref Mod.VendorHandovers);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] vendor handover{(sameHousehold ? " (SAME HOUSEHOLD)" : "")}: " +
                $"{sale.Character} sold {sale.Item} to {sale.Vendor}, {player.Name} bought it " +
                $"{age.TotalSeconds:0}s later");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] vendor buy watch failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>Sales nobody bought inside the window stop being interesting.</summary>
    private static void Prune()
    {
        if ((DateTime.UtcNow - _lastPrune).TotalSeconds < 60)
            return;

        _lastPrune = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(1, Mod.Settings.VendorHandoverWindowSeconds));

        foreach (var kv in _onVendor)
            if (kv.Value.When < cutoff)
                _onVendor.TryRemove(kv.Key, out _);
    }

    public static void Clear() => _onVendor.Clear();

    public static int Watching => _onVendor.Count;
}
