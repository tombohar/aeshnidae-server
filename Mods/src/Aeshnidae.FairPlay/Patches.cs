namespace Aeshnidae.FairPlay;

/// <summary>
/// The four rules. Every patch is a postfix that either observes or flips a boolean -
/// none of them replace ACE's logic, so when this mod is disabled the server behaves
/// exactly as stock.
///
/// Enforcement is announced through <c>PlayerManager.BroadcastToAuditChannel</c>
/// rather than written to a private log. Aeshnidae.AdminAudit already hooks that
/// channel, so every bounce and refusal lands in the audit JSONL and the Discord
/// audit feed for free - and because PlayerManager is a host type, this works without
/// the two mods referencing each other across assembly load contexts.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    // ------------------------------------------------------------ character cap

    /// <summary>
    /// Staff are never "at max characters".
    ///
    /// The numeric cap itself stays ACE's <c>max_chars_per_account</c> property -
    /// Mod.Initialize sets it - so there is one source of truth for the number. All
    /// this does is exempt privileged accounts, which a global property cannot express.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.IsAccountAtMaxCharacterSlots))]
    public static void PostIsAccountAtMaxCharacterSlots(string accountName, ref bool __result)
    {
        try
        {
            if (!Mod.Settings.Enabled || !__result || string.IsNullOrEmpty(accountName))
                return;

            var account = DatabaseManager.Authentication.GetAccountByName(accountName);

            if (account is null)
                return;

            if ((AccessLevel)account.AccessLevel >= Mod.Settings.ExemptLevel)
            {
                __result = false;
                ModManager.Log($"[{Mod.Name}] character cap waived for {accountName} " +
                               $"({(AccessLevel)account.AccessLevel})");
            }
        }
        catch (Exception ex)
        {
            // Never block character creation because this mod had a bad day.
            ModManager.Log($"[{Mod.Name}] character-cap check failed for {accountName}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }

    // --------------------------------------------------------- account creation

    /// <summary>
    /// Caps how many accounts one address may create.
    ///
    /// Auto-creation is on, so without this anyone who can reach the login port can
    /// mint accounts endlessly - a spam vector, and a way to bury the household graph
    /// in noise. ACE records CreateIP on every account, so the count is simply a query.
    ///
    /// A prefix returning false skips creation and yields null, which ACE's login path
    /// turns into a failed login. The player sees a generic failure rather than a
    /// tailored message; the real reason goes to the audit channel so staff can see it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(AuthenticationDatabase), nameof(AuthenticationDatabase.CreateAccount))]
    public static bool PreCreateAccount(string name, IPAddress address, ref Account __result)
    {
        try
        {
            if (!Mod.Settings.Enabled || Mod.Settings.MaxAccountsPerAddress <= 0 || address is null)
                return true;

            var ip = address.ToString();

            if (Mod.Settings.IsAddressExempt(ip))
                return true;

            var existing = Mod.CountAccountsCreatedFrom(address);

            if (existing < Mod.Settings.MaxAccountsPerAddress)
                return true;

            Interlocked.Increment(ref Mod.AccountsRefused);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] refused new account \"{name}\" from {ip} - " +
                $"{existing} accounts already created there, limit {Mod.Settings.MaxAccountsPerAddress}");

            __result = null!;
            return false;   // skip ACE's creation
        }
        catch (Exception ex)
        {
            // Never stop legitimate account creation because this check failed.
            ModManager.Log($"[{Mod.Name}] account-cap check failed: {ex.Message}", ModManager.LogLevel.Error);
            return true;
        }
    }

    // -------------------------------------------------------------- marketplace

    /// <summary>
    /// Fires after every teleport lands - portal, recall, summon, admin teleport - so
    /// it catches every way out of the Marketplace without needing a patch per route.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.OnTeleportComplete))]
    public static void PostOnTeleportComplete(Player __instance) => MarketplaceRule.Enforce(__instance, "teleport");

    /// <summary>Catches logging in outside the Marketplace while another character is already out.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.PlayerEnterWorld))]
    public static void PostPlayerEnterWorld(Player __instance)
    {
        try
        {
            Mod.RecordLogin(__instance);

            // Give the client a moment to finish entering before acting on it.
            var chain = new ActionChain();
            chain.AddDelaySeconds(3.0);
            chain.AddAction(__instance, () =>
            {
                // Concurrency first: no point bouncing someone to the Marketplace if
                // they are about to be logged out for being the third one on.
                if (!ConcurrencyRule.Enforce(__instance))
                    MarketplaceRule.Enforce(__instance, "login");
            });
            chain.EnqueueChain();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] login handling failed for {__instance?.Name}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------- ground

    /// <summary>
    /// Records what goes on the ground. A prefix, because once the drop completes the
    /// item has left the inventory and the guid no longer resolves for a description.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionDropItem))]
    public static void PreDropItemWatch(Player __instance, uint itemGuid)
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.FlagGroundTransfers)
                return;

            var item = __instance.FindObject(itemGuid,
                Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);

            var what = item is null
                ? $"0x{itemGuid:X8}"
                : $"{item.Name}{(item.StackSize > 1 ? $" x{item.StackSize}" : "")} (wcid {item.WeenieClassId})";

            GroundWatch.OnDrop(__instance, itemGuid, what);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] drop capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Both halves of a custody transfer, from the one place that knows where an item
    /// came from and where it went.
    ///
    /// This method is private in ACE, but it is the only seam carrying both
    /// <c>itemRootOwner</c> and <c>containerRootOwner</c> - which is exactly what
    /// separates "moved something between my own packs" from "put something in a
    /// housing chest". The public entry point does not have that, and without it a
    /// chest deposit is indistinguishable from tidying your backpack.
    ///
    /// A housing chest matters more than the ground, because the item sits there safely
    /// until the other character logs in - no timing needed, no risk of a passer-by
    /// taking it.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), "DoHandleActionPutItemInContainer")]
    public static void PostDoPutItemInContainer(Player __instance, WorldObject item,
                                                Container itemRootOwner, Container containerRootOwner,
                                                bool __result)
    {
        try
        {
            if (!__result || item is null || !Mod.Settings.Enabled || !Mod.Settings.FlagGroundTransfers)
                return;

            var intoMine = ReferenceEquals(containerRootOwner, __instance);
            var fromMine = ReferenceEquals(itemRootOwner, __instance);

            // Shuffling between your own packs is not a custody change.
            if (intoMine && fromMine)
                return;

            var what = $"{item.Name}{(item.StackSize > 1 ? $" x{item.StackSize}" : "")} (wcid {item.WeenieClassId})";

            if (!intoMine)
            {
                // Leaving my possession into something another character can open.
                var where = containerRootOwner is null
                    ? (item.CurrentLandblock is not null ? "the ground" : "a container")
                    : $"{containerRootOwner.Name}";

                GroundWatch.OnRelease(__instance, item.Guid.Full, what, where);
            }
            else if (!fromMine)
            {
                // Coming into my possession from outside it - ground, chest, corpse.
                GroundWatch.OnPickup(__instance, item.Guid.Full);
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] container-move watch failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------- vendor

    /// <summary>
    /// What the vendor kept. ProcessItemsForPurchase decides per item whether to resell
    /// it or destroy it, so the ones still in UniqueItemsForSale afterwards are the ones
    /// another character can buy.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Vendor), nameof(Vendor.ProcessItemsForPurchase))]
    public static void PostProcessItemsForPurchase(Vendor __instance, Player player, Dictionary<uint, WorldObject> items)
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.FlagVendorHandovers || items is null)
                return;

            foreach (var item in items.Values)
            {
                if (item is not null && __instance.UniqueItemsForSale.ContainsKey(item.Guid))
                    VendorWatch.OnSell(player, item, __instance.Name);
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] vendor sell capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Unique items reaching a buyer. A prefix, taking the guids before the purchase
    /// completes - FinalizeBuyTransaction removes each one from the vendor as it lands.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.FinalizeBuyTransaction))]
    public static void PreFinalizeBuyTransaction(Player __instance, List<WorldObject> uniqueItems)
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.FlagVendorHandovers || uniqueItems is null)
                return;

            foreach (var item in uniqueItems)
            {
                if (item is not null)
                    VendorWatch.OnBuy(__instance, item.Guid.Full);
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] vendor buy capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------ trading

    /// <summary>
    /// Flags - does not block - an item handed from one character to another in the
    /// same household.
    ///
    /// Moving your own gear to your own mule is ordinary play, so refusing it would be
    /// obnoxious. What is worth seeing is the pattern: AdminAudit already records every
    /// give, and tagging the linked ones turns that into "this mule received fourteen
    /// items from its own main tonight", which is the evidence you would actually act on.
    ///
    /// A prefix, because by the time the handover completes the item may have moved and
    /// the guid no longer resolves.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    public static void PreGiveObjectRequest(Player __instance, uint targetGuid, uint itemGuid, int amount)
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.FlagLinkedTrades)
                return;

            var target = PlayerManager.GetOnlinePlayer(new ObjectGuid(targetGuid));

            if (target is null || target.Guid.Full == __instance.Guid.Full)
                return;   // giving to an NPC or to yourself is not interesting

            // Audits cover staff. They are precisely who a trail exists to cover.
            var giver = Mod.IdentityOf(__instance);
            var receiver = Mod.IdentityOf(target);

            if (giver is null || receiver is null || giver != receiver)
                return;

            var item = __instance.FindObject(itemGuid,
                Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);

            var what = item is null
                ? $"0x{itemGuid:X8}"
                : $"{item.Name}{(amount > 1 ? $" x{amount}" : "")} (wcid {item.WeenieClassId})";

            Interlocked.Increment(ref Mod.LinkedTrades);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] linked trade: {__instance.Name} -> {target.Name}, {what} ({giver})");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] linked-trade flag failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // --------------------------------------------------------------- allegiance

    /// <summary>
    /// Refuses a pledge between two characters on the same address.
    ///
    /// <c>IsPledgable</c> is where ACE already collects every reason a pledge cannot
    /// happen and messages the player, so adding one more reason here behaves exactly
    /// like the built-in ones. <c>__instance</c> is the would-be vassal.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.IsPledgable))]
    public static void PostIsPledgable(Player __instance, Player target, ref bool __result)
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.BlockSameIpAllegiance || !__result)
                return;

            // No staff exemption: a pledge between two characters on one household is
            // the same thing whoever is holding the accounts.

            // Household rather than live address, so moving one client onto a VPN no
            // longer unlinks the pair.
            var vassal = Mod.IdentityOf(__instance);
            var patron = Mod.IdentityOf(target);

            if (vassal is null || patron is null || vassal != patron)
                return;

            __result = false;
            Interlocked.Increment(ref Mod.PledgesBlocked);
            __instance.SendMessage(Mod.Settings.AllegianceBlockedMessage, ChatMessageType.Broadcast);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] refused allegiance: {__instance.Name} -> {target.Name}, same identity ({vassal})");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] allegiance check failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }
}

/// <summary>
/// "At most N characters may be logged in at once from one address."
///
/// This is the limit that actually matters on this server: players may create as many
/// characters and hold as many accounts as they like, but simultaneous presence is
/// rationed. Keyed on address for the same reason as the Marketplace rule - a second
/// account on the same machine is the same person.
///
/// The newest arrival is the one logged out, because they are the one that broke the
/// rule, and because logging out someone mid-fight to make room for a fresh login
/// would be worse.
/// </summary>
internal static class ConcurrencyRule
{
    /// <summary>Returns true if the player was logged out for exceeding the limit.</summary>
    public static bool Enforce(Player? player)
    {
        try
        {
            if (player?.Session is null || !Mod.Settings.Enabled || Mod.Settings.MaxConcurrentPerAddress <= 0)
                return false;

            if (Mod.Settings.IsLimitExempt(player))
                return false;

            var address = Mod.AddressOf(player);

            if (string.IsNullOrEmpty(address) || Mod.Settings.IsAddressExempt(address))
                return false;

            var identity = Mod.IdentityOf(player);

            if (string.IsNullOrEmpty(identity))
                return false;

            var online = PlayerManager.GetAllOnline()
                .Where(p => p is not null
                            && !Mod.Settings.IsLimitExempt(p)
                            && string.Equals(Mod.IdentityOf(p), identity, StringComparison.Ordinal))
                .ToList();

            if (online.Count <= Mod.Settings.MaxConcurrentPerAddress)
                return false;

            var message = string.Format(Mod.Settings.TooManyOnlineMessage, Mod.Settings.MaxConcurrentPerAddress);
            player.SendMessage(message, ChatMessageType.Broadcast);

            Interlocked.Increment(ref Mod.LogoutsForced);

            PlayerManager.BroadcastToAuditChannel(null,
                $"[FairPlay] logged out {player.Name} - {online.Count} characters online for {identity}, " +
                $"limit {Mod.Settings.MaxConcurrentPerAddress}");

            // A short delay so the message actually reaches the client before the
            // session ends. LogOffPlayer rather than Terminate: it saves the character.
            var chain = new ActionChain();
            chain.AddDelaySeconds(2.0);
            chain.AddAction(player, () =>
            {
                try { player.Session?.LogOffPlayer(); }
                catch (Exception ex)
                {
                    ModManager.Log($"[{Mod.Name}] could not log off {player.Name}: {ex.Message}",
                                   ModManager.LogLevel.Error);
                }
            });
            chain.EnqueueChain();

            return true;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] concurrency check failed for {player?.Name}: {ex.Message}",
                           ModManager.LogLevel.Error);
            return false;
        }
    }
}

/// <summary>
/// "At most one character per IP address may be outside the Marketplace."
///
/// Keyed on address rather than account, because the thing being limited is a person,
/// and a second account on the same machine is the same person.
///
/// Deliberately stateless: nothing marks a character as the mule. The rule is worked
/// out live from who is online and where they are, which means swapping happens by
/// itself - bring both into the Marketplace, walk either one out, and that one is now
/// the character that is out. Whoever is already outside keeps their place; the one
/// who tries to join them is the one sent back.
///
/// The cost of an address-based rule: people who genuinely share a connection - a
/// household, a shared flat - look like one person. ExemptAddresses is the escape
/// hatch for those.
/// </summary>
internal static class MarketplaceRule
{
    public static void Enforce(Player? player, string via)
    {
        try
        {
            if (player?.Session is null || !Mod.Settings.Enabled || !Mod.Settings.OneCharacterOutsideMarketplace)
                return;

            if (Mod.Settings.MarketplaceExemptsStaff && Mod.Settings.IsLimitExempt(player))
                return;

            var mp = Mod.Settings.Marketplace;

            // Already in the Marketplace: nothing to do. This is also what stops the
            // bounce from recursing, since the bounce lands them here.
            if (LandblockOf(player) == mp.LandblockId)
                return;

            var address = Mod.AddressOf(player);

            if (string.IsNullOrEmpty(address) || Mod.Settings.IsAddressExempt(address))
                return;

            var identity = Mod.IdentityOf(player);

            if (string.IsNullOrEmpty(identity))
                return;

            // Is another character from the same household already outside? Keyed on
            // household rather than account or live address: the point is one *person*
            // out at a time, and neither a second account nor a VPN changes the person.
            var otherOutside = PlayerManager.GetAllOnline()
                .FirstOrDefault(p => p is not null
                                     && p.Guid.Full != player.Guid.Full
                                     && string.Equals(Mod.IdentityOf(p), identity, StringComparison.Ordinal)
                                     && LandblockOf(p) != mp.LandblockId);

            if (otherOutside is null)
                return;   // this one is the single character out - allowed

            Bounce(player, via, $"{otherOutside.Name} is already out for {identity}");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] marketplace rule failed for {player?.Name}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Checks everyone online, not just whoever just moved.
    ///
    /// The event hooks only fire on travel, so they cannot see a character who was
    /// already standing outside when the mod loaded, or who left by a route with no
    /// hook. This makes the rule continuously true rather than true-at-the-moment-of-
    /// travel, which is the difference between a rule and a tripwire.
    /// </summary>
    public static void Sweep()
    {
        try
        {
            if (!Mod.Settings.Enabled || !Mod.Settings.OneCharacterOutsideMarketplace)
                return;

            var mp = Mod.Settings.Marketplace;
            var keepNewest = !string.Equals(Mod.Settings.KeepOutside, "OldestLogin", StringComparison.OrdinalIgnoreCase);

            var offenders = PlayerManager.GetAllOnline()
                .Where(p => p is not null
                            && !(Mod.Settings.MarketplaceExemptsStaff && Mod.Settings.IsLimitExempt(p))
                            && !string.IsNullOrEmpty(Mod.IdentityOf(p))
                            && !Mod.Settings.IsAddressExempt(Mod.AddressOf(p))
                            && LandblockOf(p) != mp.LandblockId)
                .GroupBy(p => Mod.IdentityOf(p)!, StringComparer.Ordinal)
                .Where(g => g.Count() > 1);

            foreach (var group in offenders)
            {
                // LoginTimestamp is when this session began, so the smallest value is
                // the character that has been logged in longest.
                var ordered = group.OrderBy(p => p.LoginTimestamp ?? 0d).ToList();

                // Keep exactly one outside; everyone else goes back.
                var keeper = keepNewest ? ordered[^1] : ordered[0];

                foreach (var p in ordered.Where(p => p.Guid.Full != keeper.Guid.Full))
                    Bounce(p, "sweep", $"{keeper.Name} keeps the slot for {group.Key}");
            }
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] marketplace sweep failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    private static void Bounce(Player player, string via, string because)
    {
        var mp = Mod.Settings.Marketplace;

        var destination = new Position(mp.CellId, mp.X, mp.Y, mp.Z,
                                       mp.RotationX, mp.RotationY, mp.RotationZ, mp.RotationW);

        // Always go through an ActionChain. The teleport hook runs on the world thread
        // already, but Sweep() runs on a timer thread, and moving a player between
        // landblocks off-thread is a good way to corrupt landblock state. Enqueueing
        // costs nothing on the path that was already safe.
        var chain = new ActionChain();
        chain.AddAction(player, () =>
        {
            try
            {
                player.SendMessage(mp.BounceMessage, ChatMessageType.Broadcast);
                player.Teleport(destination);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] bounce of {player.Name} failed: {ex.Message}",
                               ModManager.LogLevel.Error);
            }
        });
        chain.EnqueueChain();

        Interlocked.Increment(ref Mod.Bounces);

        PlayerManager.BroadcastToAuditChannel(null,
            $"[FairPlay] returned {player.Name} to the Marketplace ({via}); {because}");
    }

    /// <summary>Landblock id (high 16 bits of the cell), or 0 when the player has no position yet.</summary>
    private static ushort LandblockOf(Player? player)
    {
        try { return (ushort)((player?.Location?.Cell ?? 0u) >> 16); }
        catch { return 0; }
    }
}
