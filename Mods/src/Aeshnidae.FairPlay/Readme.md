# Aeshnidae.FairPlay

One human, one presence. Limits on how many characters one person can have in the
world at once, and a record of the ways people move items and experience between
their own characters. `/fairplay` shows the rules and the counters; `/fairplay shared`
the addresses used by several accounts; `/fairplay-reload` re-reads `Settings.json`.

## The rules (limits)

| Rule | Setting |
| --- | --- |
| At most 2 characters logged in at once from one address; the third is logged straight back out | `MaxConcurrentPerAddress` |
| At most one character per address outside the Marketplace; whoever travels out second is sent back | `OneCharacterOutsideMarketplace`, `Marketplace` |
| An allegiance pledge between characters of the same household is refused | `BlockSameIpAllegiance` |
| At most 3 accounts created from one address | `MaxAccountsPerAddress` |

Staff at `ExemptAtOrAbove` (Developer) skip the limits, never the records. Addresses
in `ExemptAddresses` are households of more than one person and skip the Marketplace
rule and the linking.

## The records (flags, never blocks)

Every one of these is a line in the `#fairplay` channel, routed there by
Aeshnidae.AdminAudit on its `[FairPlay]` prefix.

| What | When it fires | Setting |
| --- | --- | --- |
| Linked trade | an item handed between two characters of the same household | `FlagLinkedTrades` |
| Ground transfer | one character drops an item, or puts it in a housing chest, and a different character collects it within the window | `FlagGroundTransfers`, `GroundTransferWindowSeconds` |
| Vendor handover | one character sells an item to a vendor and a different character buys it within the window | `FlagVendorHandovers`, `VendorHandoverWindowSeconds` |
| VPN login | a login from a listed VPN or datacentre range | `FlagVpnLogins` |
| Shared address | one address seen with more than N accounts, or one account from more than N addresses | `FlagAccountsPerAddress`, `FlagAddressesPerAccount` |

`SAME HOUSEHOLD` on a transfer line means both characters belong to accounts that have
shared an address.

## Households

A household is the set of accounts that have ever logged in from a common address
(`LinkAccountsBySharedAddress`), which is what stops every rule above being defeated
by a VPN: once two accounts have been seen together they stay linked. VPN and
datacentre addresses (`UseVpnLists`, `ExcludeVpnFromLinking`) never link accounts, so
strangers sharing an exit node are not welded together. `NeverLink` pins an account to
a household of one. Login history is kept for `HistoryDays`.

## Files

| File | |
| --- | --- |
| `Patches.cs` | The hooks: login, teleport, allegiance, trade, drop, container, vendor. |
| `GroundWatch.cs` | Ground and container handovers. |
| `VendorWatch.cs` | Vendor handovers. |
| `IpRegistry.cs` | Login addresses and the household graph. |
| `VpnList.cs` | The CIDR lists. |
| `Settings.cs` | Every knob, documented. |
