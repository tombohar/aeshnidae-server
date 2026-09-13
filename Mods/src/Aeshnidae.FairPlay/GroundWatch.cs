namespace Aeshnidae.FairPlay;

/// <summary>
/// Watches items that go onto the ground and who picks them up.
///
/// Dropping is the obvious way around trade logging: hand nothing to anyone, just put
/// it down and walk away while your mule collects it. No trade event, no give event,
/// nothing for the linked-trade flag to see. A housing chest is the same move with
/// less walking - and better for the launderer, because the item sits there safely
/// until the other character logs in.
///
/// Both are the same shape, so both go through here: an item leaves your possession
/// into somewhere another character can reach, and later a different character takes
/// it out.
///
/// A drop on its own is not evidence - players discard junk constantly - so nothing is
/// announced for a drop alone unless FlagAllDrops is set. What gets reported is the
/// completed handover: dropped by one character, collected by another, inside a time
/// window. When both belong to the same household that is worth reading closely; when
/// they do not, it is still worth knowing, because that is what selling outside the
/// trade window looks like.
/// </summary>
internal static class GroundWatch
{
    private sealed record Dropped(string Identity, string Character, string Item, string Where, DateTime When);

    /// <summary>item guid -> who put it down. Small and short-lived by construction.</summary>
    private static readonly ConcurrentDictionary<uint, Dropped> _onGround = new();

    private static DateTime _lastPrune = DateTime.UtcNow;

    public static void OnDrop(Player? player, uint itemGuid, string itemDescription)
        => OnRelease(player, itemGuid, itemDescription, "the ground");

    /// <summary>
    /// An item leaving the player's possession into somewhere another player can reach
    /// it - the ground, a housing chest, any external container.
    /// </summary>
    public static void OnRelease(Player? player, uint itemGuid, string itemDescription, string where)
    {
        try
        {
            if (player is null || !Mod.Settings.Enabled || !Mod.Settings.FlagGroundTransfers)
                return;

            // No staff exemption - this is a record, not a restriction.
            var identity = Mod.IdentityOf(player);

            if (identity is null)
                return;

            _onGround[itemGuid] = new Dropped(identity, player.Name, itemDescription, where, DateTime.UtcNow);

            if (Mod.Settings.FlagAllDrops)
                PlayerManager.BroadcastToAuditChannel(null,
                    $"[FairPlay] {player.Name} placed {itemDescription} in {where}");

            Prune();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] drop watch failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    public static void OnPickup(Player? player, uint itemGuid)
    {
        try
        {
            if (player is null || !Mod.Settings.Enabled || !Mod.Settings.FlagGroundTransfers)
                return;

            // Only interesting if this exact item was recently put down. Everything
            // else moving through inventory is ordinary and never reaches here.
            if (!_onGround.TryRemove(itemGuid, out var drop))
                return;

            var age = DateTime.UtcNow - drop.When;

            if (age.TotalSeconds > Math.Max(1, Mod.Settings.GroundTransferWindowSeconds))
                return;

            // Picking your own item back up is not a handover.
            if (string.Equals(player.Name, drop.Character, StringComparison.OrdinalIgnoreCase))
                return;

            var identity = Mod.IdentityOf(player);
            var sameHousehold = identity is not null && identity == drop.Identity;

            Interlocked.Increment(ref Mod.GroundTransfers);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] ground transfer{(sameHousehold ? " (SAME HOUSEHOLD)" : "")}: " +
                $"{drop.Character} left {drop.Item} in {drop.Where}, {player.Name} collected it " +
                $"{age.TotalSeconds:0}s later");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] pickup watch failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>Drops nobody collected inside the window stop being interesting.</summary>
    private static void Prune()
    {
        if ((DateTime.UtcNow - _lastPrune).TotalSeconds < 60)
            return;

        _lastPrune = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(1, Mod.Settings.GroundTransferWindowSeconds));

        foreach (var kv in _onGround)
            if (kv.Value.When < cutoff)
                _onGround.TryRemove(kv.Key, out _);
    }

    public static void Clear() => _onGround.Clear();

    public static int Watching => _onGround.Count;
}
