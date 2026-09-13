# Aeshnidae.QuestBonus

Solved quests accumulate **quest points** on the character, which convert into a
small permanent XP multiplier.

Ported from Aquafir's `Samples/QuestBonus` in `ACE.BaseMod`, with the `ACE.Shared`
dependency removed and three bugs fixed (see below).

## Tuning

Default is **+0.1% XP per solved quest**, capped at 2x.

Edit `C:\ACEPublic\Mods\Aeshnidae.QuestBonus\Settings.json` (written on first run),
then `/qbreload`. Changes take effect immediately and every online character is
resynced.

| Setting | Default | Meaning |
| --- | --- | --- |
| `BonusConversion` | `0.001` | Multiplier gained per quest point. `0.001` = +0.1%. |
| `DefaultPoints` | `1.0` | Points for a solved quest with no explicit weight. |
| `MaxMultiplier` | `2.0` | Ceiling on the multiplier. `0` disables the cap. |
| `NotifyQuest` | `true` | Master switch for the two messages below. |
| `QuestGainedMessage` | see below | Shown when a quest starts counting. `""` to stay silent. |
| `QuestLostMessage` | see below | Shown when a quest stops counting. |
| `NotifyExp` | `false` | Announce every XP award's boost. Very chatty. |
| `QuestWeights` | 3 exclusions | Per-quest overrides by name. `0` excludes a quest. |

At the default rate: 50 quests = +5%, 200 quests = +20%. The cap only bites at
1,000 quest points.

### Messages

`QuestGainedMessage` and `QuestLostMessage` support four placeholders:

| | |
| --- | --- |
| `{quest}` | Quest name, e.g. `ChasingOswaldDone` |
| `{points}` | Points gained or lost |
| `{bonus}` | New total bonus, e.g. `+4.2%` |
| `{qp}` | New total quest points |

Defaults:

```
Way to go ya filthy animal.. You've gained QB by completing {quest}!
You've lost {points} QB from {quest}. Now at {bonus} XP.
```

`Quests.txt` (in this source folder, not deployed) lists all 4,180 known quest
names in `"Name": 0,` form, ready to paste into `QuestWeights`. Name matching is
case-insensitive.

## Commands

| Command | Access | |
| --- | --- | --- |
| `/qb` | Player | Quests solved, points, current bonus. |
| `/qb list` | Player | Per-quest breakdown with weights. |
| `/qb sync` | Player | Recalculate from the quest registry. |
| `/qbreload` | Admin | Re-read `Settings.json`, resync everyone online. |

Aquafir's original used `/qp` for the summary and `/qb` for the list; this
consolidates both under `/qb`.

## How it works

Points live on the character as `PropertyFloat` **20002** — a "fake" property id
outside the range ACE uses, so the shard DB persists it like any other. That is
deliberately the same id `ACE.Shared`'s `FakeFloat.QuestBonus` uses, so the data is
interchangeable with Aquafir's mods.

A quest is only worth points when it crosses **unsolved → solved**. Repeat
completions add nothing, which is what stops timers and dailies from inflating the
total. All four `QuestManager` mutators (`Update`, `SetQuestCompletions`,
`Decrement`, `Erase`) share one prefix/postfix pair that compares solve counts
before and after.

The total is recalculated from scratch on login, so hand-edited or imported
characters self-correct.

## Fixes vs. the original

1. **Login never resynced.** The original postfix called `CalculateQuestPoints()`,
   which is a pure sum, and discarded the result — the stored property was never
   written. Now calls `ResyncQuestPoints()`.
2. **Decrement added points instead of removing them.** `PreDecrement` called
   `IncQuestPoints(+weight)` on quest removal, where `PreErase` correctly used
   `-weight`. Both now go through the same before/after comparison.
3. **The XP bonus applied twice in a fellowship.** `Player.GrantXP` re-enters
   itself: with an XP-sharing fellowship it defers to `Fellowship.SplitXp`, which
   calls `GrantXP` again per member with `ShareType.Fellowship` cleared. The
   original prefix multiplied on both passes, squaring the bonus. The outer call is
   now skipped, so each member's share is scaled by their own bonus.
