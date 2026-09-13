# Aeshnidae.MaxLevel

Raises the character level cap from retail's **275** to **500**, continuing the
retail XP curve rather than inventing a new one, and granting a skill credit every
25 levels past 275.

## Settings

`Mods\Aeshnidae.MaxLevel\Settings.json`:

| Setting | Default | |
| --- | --- | --- |
| `MaxLevel` | `500` | New cap. At or below 275 the table is left untouched. |
| `SkillCreditInterval` | `25` | Grant a credit every N levels past 275. |
| `SkillCreditsPerInterval` | `1` | Credits per milestone. |

Changing these needs a **server restart** — the XP table is only rebuilt when the
dats load. `/maxlevel` shows the current cap and level costs.

## How it works

`Player.GetMaxLevel()` is literally:

```csharp
return (uint)DatManager.PortalDat.XpTable.CharacterLevelXPList.Count - 1;
```

So the cap is just that list's length. `CharacterLevelXPList` and
`CharacterLevelSkillCreditList` are `{ get; }` List properties — the objects are
mutable even though the properties are read-only — so the mod appends entries to
both in memory. No Harmony patch on the level logic itself, and nothing on disk is
modified: the dat files are untouched and the change lives only in the running
process.

Both lists must grow together. `CheckForLevelup` indexes
`CharacterLevelSkillCreditList[Level]`, so extending only the XP list would throw
`IndexOutOfRangeException` the moment someone hit 276.

### Timing

`ModManager.Initialize()` runs at `Program.cs:158`, but `DatManager.Initialize()`
not until line 239 — when the mod starts, `DatManager.PortalDat` is still null and
there is no table to extend. So `Patches.cs` puts a postfix on
`DatManager.Initialize` and does the work the instant the dats finish loading.
`Mod.Initialize` also applies it directly, which covers `/mod restart` on a running
server where `DatManager.Initialize` will not fire again.

`Dispose` reverts the lists to exactly what the dat shipped, so unloading leaves no
trace and a reload cannot stack entries.

## The curve

The retail table's level costs obey a clean growth law:

```
delta[L] / delta[L-1] == (L + 6) / (L + 2)
```

At L=275 that predicts `1.014440433` against `1.014440153` measured — the drift at
low levels is just integer rounding in the dat. Continuing that same recurrence is
what "keep retail scaling" means here, and it makes the seam at 275→276 exactly as
smooth as 274→275.

Measured after extension, predicted vs actual agree to nine decimals:

| Level | Total XP | Cost of that level | Ratio |
| --- | --- | --- | --- |
| 275 (retail cap) | 191,226,310,247 | 3,390,451,400 | 1.014440153 |
| 276 | 194,665,545,120 | 3,439,234,873 | 1.014388489 |
| 300 | 293,262,955,040 | 4,776,190,615 | 1.013245033 |
| 350 | 626,472,578,842 | 8,774,057,810 | 1.011363636 |
| 400 | 1,210,703,467,423 | 14,873,447,374 | 1.009950249 |
| 450 | 2,166,814,012,073 | 23,706,883,601 | 1.008849558 |
| 500 | 3,649,413,900,316 | 35,990,227,313 | 1.007968127 |

275 → 500 costs **3,458,187,590,069 XP**, roughly 18x the entire retail grind to
275. `TotalExperience` is a `PropertyInt64`, and 3.65e12 sits comfortably inside
`long.MaxValue` (9.22e18).

## Skill credits

Levels **300, 325, 350, 375, 400, 425, 450, 475, 500** each grant 1, so a level 500
character has **55** total against retail's 46.

`DeveloperFixCommands.AdditionalCredits` is also extended. That table maps level →
expected total credits and stops at `{275, 46}`; `GetAdditionalCredits` walks it in
reverse and returns the first entry with `level >= key`, so without this every
character above 275 would look like it had too many credits and
`verify-skill-credits fix` would strip exactly the ones this mod granted. The mod
adds `300=47 … 500=55` and removes them again on unload.

## Caveat: the client

This is a server-side change. The AC client has its own copy of the XP table from
its own `client_portal.dat`, which still stops at 275. The level number itself comes
from the server as a property and displays fine, but client-side XP-to-next-level
UI above 275 is reading a table that has no entry for those levels, and I have not
been able to verify how it behaves. Worth a look on the first character that gets
there.
