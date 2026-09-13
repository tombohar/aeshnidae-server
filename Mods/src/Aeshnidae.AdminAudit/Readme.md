# Aeshnidae.AdminAudit

A record of what privileged accounts do on the server: every command, what it created,
gave away, dropped or granted, and every bank, XP-currency and instance event.

```
/adminaudit                status, counters, sink health
/adminaudit tail [n]       last n records
/adminaudit find <text> [n]  search the trail
/adminaudit hooks          which sibling-mod hooks are bound
/adminaudit rebind         re-bind them after /mod find
/adminaudit-reload         restart the mod, re-reading Settings.json
```

All Admin-only — the trail names who did what, and its status tells you whether anyone
would notice if you did something.

## What gets recorded

| Source | Covers |
| --- | --- |
| `CommandManager.GetCommandHandler` | **Every** command, console and in-game, allowed **and refused** |
| `PlayerManager.BroadcastToAuditChannel` | The 44 actions ACE already narrates: delete, smite, teleport, server properties, shutdown, housing, events |
| `Player.TryCreateInInventoryWithNetworking` | Objects conjured into an inventory (`/ci`) |
| `Player.HandleActionDropItem` | A privileged character putting something on the ground |
| `Player.HandleActionGiveObjectRequest` | A privileged character handing something to a player or NPC |
| `Player.GrantXP` / `GrantLuminance` | XP and luminance granted by command |
| `Aeshnidae.Bank.BankService` | Deposits and withdrawals, with the refusal reason when one fails |
| `Aeshnidae.XpCurrency.Transfer` | XP transfers between players |
| `Aeshnidae.InstancesNoDat.InstanceWorld` | Landblock copies being created |

Refused commands are recorded even for unprivileged accounts. A plain player reaching
for `@ci` is the single most interesting thing this mod can tell you, and it would be
lost if the access filter ran first.

## The trail itself

One JSON object per line, one file per UTC day, in `Mods\Aeshnidae.AdminAudit\audit\`:

```json
{"ts":"2026-09-10T01:14:22.7Z","kind":"Command","actor":"+Buffy","account":"aeshtest1",
 "access":"Admin","source":"ingame","action":"ci","detail":"@ci 30977 5","outcome":"ok",
 "loc":"0x2B... [12.3 45.6 78.9]","readonly":false,"data":{"requires":"Developer"}}
```

Append-only and never rewritten. Greppable with ordinary tools:

```bash
grep '"actor":"+Buffy"' audit/audit-20260910.jsonl | grep -v '"readonly":true'
```

`account` matters more than `actor`: a character can be renamed, an account cannot.

Written as UTF-8 with **no** byte-order mark: `Encoding.UTF8` emits one when it creates
a file, and those three bytes sit in front of the first record where System.Text.Json
shrugs them off but Python's `json` and some `jq` builds refuse the line. A trail whose
point is being readable by ordinary tools cannot start by breaking them.

### What does not reach the journal

A command needing no more than **Player** access is not a use of privilege - it is
something any player could do. Journalling those buries the trail: an admin who plays
normally generates them constantly, and a client plugin polling `/b` on a timer produces
one a minute forever. So they are skipped, controlled by `JournalPlayerLevelCommands`
(default `false`).

Nothing substantive is lost, because the actions that matter have their own hooks: a
bank move is recorded by `BankService`, an XP transfer by `Transfer.Send`, a conjured
item by the inventory hook - each with the amount, the target and the outcome, which is
better evidence than an echo of the command line. Only the echo goes.

Two things always survive the filter: **refused** commands at any access level, and the
outcome hooks, which never consulted it. `AlwaysJournalCommands` re-admits specific
commands; it is empty by default, because naming commands is the wrong handle - `bank`
and its alias `b` are the same action, and listing one would make the trail depend on
which alias someone typed.

`readonly: true` marks commands that only look at things. They are still recorded — the
record of an admin probing around before acting is worth having — but the Discord feed
skips them by default and a grep can drop them. The list is in `Settings.json`; getting
it wrong costs noise, never coverage.

**Retention defaults to keeping everything** (`RetentionDays: 0`). For an audit trail,
silently discarding history is the surprising behaviour, not the safe one.

## Setup

Nothing is required — it records to file from the moment it loads. For the optional
live feed, make a webhook on a **private** channel and set:

```jsonc
"Discord": { "Enabled": true, "WebhookUrl": "https://discord.com/api/webhooks/…" }
```

then `/adminaudit-reload`. The webhook is a credential and lives only in the deployed
folder, which `Mods\.gitignore` excludes. The feed is a convenience, never the record —
if Discord is down or the webhook is revoked, the JSONL file is unaffected.

To watch one specific account regardless of its access level:

```jsonc
"AlwaysAudit": ["SuspectCharacter"]
```

## How the noisy hooks stay quiet

Two of these targets are also on ordinary gameplay paths, and would have been useless
without a discriminator.

`Player.GrantXP` runs on every kill, proficiency tick and fellowship split. The filter
is `XpType.Admin`, which appears in exactly **two** places in all of ACE — the two grant
commands. So XP auditing costs a single enum comparison on the hot path and produces no
gameplay noise at all.

`TryCreateInInventoryWithNetworking` has 29 call sites, most of them quest rewards and
salvage. The filter is a **command frame**: `GameActionTalk.Handle` is bracketed by a
prefix and postfix that set and clear a `[ThreadStatic]` record of the command being
executed, and ACE invokes command handlers synchronously on that same thread. An item
creation with no frame is gameplay; one with a frame is an admin conjuring, and the
record says which command did it.

The frame is only opened for the in-game path. Console commands are journalled but get
no frame — nothing brackets the console loop, and a frame left on that thread would
mis-attribute whatever ran next.

## The sibling-mod hooks are the fragile part

`Aeshnidae.Bank`, `Aeshnidae.XpCurrency` and `Aeshnidae.InstancesNoDat` each load into
their own collectible `AssemblyLoadContext`. This assembly cannot reference
`CurrencyKind` or `BankResult` at compile time — there is no build relationship between
the mods at all — so those targets are resolved by name at runtime and patched through
Harmony's imperative API.

Three things make that work, each verified rather than assumed:

- Harmony binds postfix parameters **by name**, and only the ones you ask for. The bank
  postfix declares `(Player player, long amount)` and simply never mentions `kind`.
- `object[] __args` carries every argument boxed, for anything that cannot be declared.
- `object __result` works for value-type returns too — Harmony boxes them.

And one that does not: **`out` parameters read through `__args` arrive as null.** Declare
them by name instead, which is possible whenever the type is a shared one —
`out string message` is just a string.

Two consequences worth knowing:

- A rename in one of those mods silently drops that hook. `/adminaudit hooks` shows
  which are bound, so this is visible rather than mysterious.
- **`/mod find` reloads every mod, which unbinds them.** Run `/adminaudit rebind`
  afterwards.

Targets are resolved through `ModManager.GetModContainerByName(...).ModAssembly`, never
by scanning `AppDomain.CurrentDomain.GetAssemblies()`. That distinction is not
cosmetic - it was a real bug. Every `/mod find` loads a fresh copy of each mod into a
new context, and the previous copy stays loaded until its context is collected. A scan
returns the **oldest** match, so the patch binds to an assembly nothing calls any more
and reports success: `/adminaudit hooks` said 4/4 bound while XP transfers went
completely unrecorded. ACE's own container is the only authority on which copy is live.

Load order still matters, because a hook can only bind once its target mod's assembly is
in memory. `ModMetadata.Priority` is a **`uint`**, and `ModManager` enables mods in
*descending* priority - so the way to load last is `Priority: 0`, not a negative number,
which fails to deserialise and costs you the entire mod for one line in the server log.

`Priority: 0` does put this mod after the three it hooks: Bank and XpCurrency are 50,
InstancesNoDat is 150. But that is their choice to change, not a guarantee, so
`Mod.ScheduleRebind` binds again 20 seconds after startup and logs any hook still
unbound. `SiblingPatches.Apply` unpatches before it patches, so those repeat passes -
and `/adminaudit rebind` - are idempotent rather than stacking duplicate postfixes.

## Limits, honestly

- **The flush interval is the durability limit.** Records are queued and written by a
  background task every second (`Log.FlushSeconds`); a hard crash loses whatever is
  still queued. `Dispose` flushes, so an orderly shutdown or `/mod disable` does not.
- **This is tamper-evident, not tamper-proof.** Anyone with filesystem access to the
  server can edit or delete the JSONL files. The Discord feed helps here: it is a copy
  an admin with shell access cannot quietly retouch. If you need more than that, ship
  the files off the box.
- **Drops and gives are recorded as performed, not as confirmed.** The handlers return
  void, so there is no cheap success signal; a drop refused deep inside ACE still leaves
  a record.
- **Console commands get no command frame,** so an item conjured from the server console
  is journalled as a command but not as an item-creation event.
- **The trail starts when the mod loads.** `audit-start` and `audit-stop` records mark
  the boundaries so a gap is distinguishable from a quiet period.
- The Discord sink is a second implementation of the webhook poster in
  `Aeshnidae.DiscordRelay` rather than a call into it — mods cannot easily call across
  load contexts, and coupling the audit trail's availability to a chat relay would be
  worse than the duplication. If a third consumer appears, extract it into a shared
  source file both projects `<Compile Include>`.

## Verified

Beyond the build, three harnesses in the scratchpad:

- **patchcheck** applies all 8 core patches against the real compiled `ACE.Server` and
  asserts each landed, including that only the two-argument
  `TryCreateInInventoryWithNetworking` overload is patched (the one-argument version
  delegates to it, so patching both would double-count every creation). It also resolves
  the four sibling targets and prints their parameter names.
- **audittest** compiles the real `AuditLog.cs` / `Settings.cs` / `AuditEvent.cs` against
  a small ACE shim: 33 checks covering JSONL round-tripping, a batch straddling midnight,
  shutdown flush, retention, tail and search, and 4,000 concurrent writes with no loss.
- **harmonyprobe** is where the cross-load-context technique above was established.
