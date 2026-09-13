# Aeshnidae.Bank

A bank shared by every character on an account.

```
/b                             balances
/b d                           deposit everything - all pyreals, luminance and keys
/b d lk 50                     deposit 50 Legendary Key uses; /b d lk for all of them
/b w p                         withdraw every banked pyreal
/b pay Aeshna rad 5m           send 5,000,000 Radiance
/bank help
```

## Commands

| | |
| --- | --- |
| `/bank`, `/b` | show balances |
| `/b d` | deposit everything the bank takes, in one go |
| `/bank deposit <item> [n]`, `/b d <item> [n]` | deposit; no amount means all |
| `/bank withdraw <item> [n]`, `/b w <item> [n]` | withdraw; no amount means all |
| `/bank pay <player> <cur> <n>`, `/b pay ...` | send Radiance or Resonance (2% fee, 100,000 minimum) |
| `/bank autolum on\|off` | earned luminance straight to the bank, past the cap |
| `/earned` | Radiance, Resonance, luminance over 5/10/30/60 min, session, per hour |
| `/bank help` | item names and short forms |
| `/bankreload` | Admin - re-read `Settings.json` |

Item names can be several words. The parser takes the first argument as the verb
and the last as the amount, then joins everything between into the item name, so
`legendary key` works without quotes.

**Items:** `pyreals` (p, py, cash, mmd) · `luminance` (lum, l) · `legendary key`
(lk, legkey) · `sturdy iron key` (sik, ik)

**Amounts:** `50`, `1,000`, `10k`, `2.5m`, or `all`.

## What each one does

**Pyreals** — deposits draw from coin *and* trade notes, smallest denominations
first so as few large notes get broken as possible. If the last note overshoots,
the difference comes back as change. Withdrawals pay out greedily from
Trade Note (250,000) down to loose Pyreal.

**Luminance** — requires attunement. A character with no `MaximumLuminance` is
told so rather than silently failing. Withdrawals are clamped to the headroom
between current and maximum luminance, because ACE's `AddLuminance` discards
anything over the cap without a word — the bank refuses instead of losing it.

**Keys** — banked as **uses, not items**, because Legendary and Sturdy Iron keys
don't stack; they carry charges in `PropertyInt.Structure`. A 25-use key banks as
25. Deposits accept every variant in the world database. Keys can't be split, so a
deposit takes whole keys smallest-first and stops before overshooting — asking to
bank 3 when your smallest key holds 25 does nothing and says why.

## Settings

`Mods\Aeshnidae.Bank\Settings.json`, then `/bankreload`.

| | Default | |
| --- | --- | --- |
| `LegendaryKeyPayout` | `[25,10,5,4,3,2,1]` | Key sizes a withdrawal may hand out, in charges |
| `SturdyIronKeyPayout` | `[50,1]` | 50 is the Keyring, 1 the plain key |
| `MaxTransaction` | `0` | Cap per transaction, 0 for none |

Payout is greedy largest-first, so this decides what you actually receive:

```
[25,10,5,4,3,2,1]   withdraw 3  -> 1x Legendary Key (3 uses)
                    withdraw 24 -> 2x Durable (10) + 1x (4 uses)
[25,1]              withdraw 3  -> 3x Legendary Key
                    withdraw 24 -> 24x Legendary Key
```

`1` is always kept in the list regardless, or a remainder could never be paid out.
Deposits accept every variant either way.

### Key variants in the world database

| Uses | wcids |
| --- | --- |
| 1 | 48747, 48914, 51558, 72048, 72600, 72628, 72669, 48746 *(Aged)* |
| 2 | 48748, 72474, 72807 |
| 3 | 48749, 51586, 51648, 72338, 72635 |
| 4 | 48750, 87168 |
| 5 | 52010 |
| 10 | 51954 *Durable Legendary Key* |
| 25 | 51963 |
| Sturdy Iron | 6876 (1 use), 23194 *Keyring* (50) |

Only 48914 and 51558 appear in loot; the rest are quest and vendor variants,
mechanically identical.

## Storage

Balances live in a table the mod creates on startup, `aeshnidae_bank` in the
**shard** database, keyed by `account_Id`. The shard db rather than a side file so
balances are covered by the same backups as the characters they belong to.
Connection details are read from ACE's own `Config.js` at runtime, so no
credentials live in the mod. Nothing in ACE's schema is modified — it is a new
table alongside.

Every adjustment is a single guarded statement:

```sql
UPDATE aeshnidae_bank SET amount = amount + @d
WHERE account_Id = @a AND currency = @c AND amount + @d >= 0;
```

The `amount + @d >= 0` clause means two characters on one account withdrawing at
the same time can't drive a balance negative — the second matches no rows and is
told the withdrawal failed.

Balances are debited **before** items are created, and re-credited if creation
fails, so a full pack can never swallow a balance. A partial payout reports how
much actually landed and returns the rest.

## Not done yet

- `MaxTransaction` is read but not yet enforced.
- No transaction log or audit trail.
- No banker NPC; commands only.
