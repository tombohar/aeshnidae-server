# Aeshnidae.Enlightenment

Enlightenment on Aeshnidae's terms. Retail's bargain was all cost; this keeps the
reset — the part that makes it mean something — and stops charging for grinds you
have already finished.

Entry is the **Font of Enlightenment and Rebirth** (wcid 53412, spawned once, in
landblock `22891` / cell `596B012A`) and nothing else.

## The terms

|  | retail | Aeshnidae |
| --- | --- | --- |
| level required | 275, exactly | 275, +1 per enlightenment held |
| maximum | 5 | uncapped (5 titles exist) |
| society rank | wiped | **kept** |
| luminance auras | wiped | **kept** |
| ability to earn luminance | wiped | **kept** |
| unassigned xp | zeroed | **kept** |
| aetheria | wiped | wiped |
| level, skills, attributes | reset | reset |
| equipped gear | moved to pack | moved to pack |
| gain | +1 all skills, +2 vitality, title, attribute certificate | same |

Every row is a field in `Settings.json`. `Enabled: false` hands the whole thing back
to retail behaviour with no other change.

`+1 to all skills` and `+2 vitality` are **not** settings — ACE derives them from
`PropertyInt.Enlightenment` in `CreatureSkill.Base` and `CreatureVital`, and they
already match the design. See the closing note in `Settings.cs` for why making them
tunable is a worse trade than it looks.

## Why the world database is untouched

Retail's requirements are not in C#. They are a `Goto`/`InqIntStat` state machine on
the Font, with the numbers in two rows of `weenie_properties_emote_action`:

```
Use → Goto "EnlightenmentCheck" → InqIntStat Enlightenment 0..4
                                → InqIntStat Level      275..275
                                → InqQuest   luminance, society
                                → InqPackSpace
                                → Goto "AbleToEnlighten" → InqYesNo → emote 9001
```

Two problems. `Level 275..275` is a *maximum* as well as a minimum, so with
`Aeshnidae.MaxLevel` raising the cap to 500 it locks out everyone past 275. And
"275 plus one per enlightenment held" is a function of player state, which one min
and one max cannot express.

So rather than migrate SQL — and re-migrate it every time a number moves — the mod
cuts the chain at its first instruction. `EmoteManager.ExecuteEmote` is prefixed;
when it sees the `Goto "EnlightenmentCheck"` aimed at a player, the mod answers the
whole question from `Settings.json` and, on success, jumps straight to the
confirmation prompt the chain would have reached anyway. Both labels are unique to
wcid 53412 in the world database, so nothing else can trip the patch.

The consequences are the point: a world reimport cannot silently revert the rules,
`/mod disable` restores retail exactly, and requirements change without touching the
database.

## The exploit this is meant to close

The old `/enlighten` command reset the character and then sent them to their
lifestone. Run it inside a dungeon and that teleport was the only thing standing
between a player and a level 1 character in high-tier content — so players ran it
in **portal space**, where the teleport could not land. They kept their position
and re-levelled off mobs far above them.

Moving to an NPC removes the command but **not the gap**. The Font's confirmation is
asynchronous: use it, get the yes/no prompt, recall or portal out, and answer yes
from somewhere else entirely. The emote fires wherever the player now is.

So `Ritual.Perform` refuses to run unless the player is still where they asked from.
Four conditions, none of them optional in spirit:

| condition | setting |
| --- | --- |
| player is in portal space (`CurrentLandblock is null`) | always refused |
| player is mid-teleport (`Teleporting`) | always refused |
| player left the Font's landblock | `RequireProximityToNpc` |
| player is >15m from the Font | `ProximityRadius` |

Refusals are logged with character, guid and reason.

`Aeshnidae.PortalAccess` closes the same hole from the other side by removing the
minimum level from all 933 gated portals — with nothing to dodge, there is nothing
to gain by dodging it. That mod is separate because it is a separate decision.

## Why there is no command

The brief asked for an NPC because a command had been abusable. Worth being precise
about what that buys:

- Stock ACE has **no** enlightenment command. `EmoteType.Enlightenment` (9001) is the
  only entry point and the Font's chain is the only thing that raises it — so the NPC
  route was already the only route.
- This mod adds no command that grants anything. `/enlighten` reports progress,
  `/enlightenment-rules` prints the active configuration, `/enlightenment-reload` is
  Admin and only re-reads settings.
- What the NPC does *not* fix by itself is re-entry — an emote chain reaching two
  confirmations can run the payload twice. In practice `ResetLevel` closes that
  window, because the second run re-checks and finds a level 1 character. That is a
  consequence of a setting, not a guarantee, so `Guard.cs` makes it explicit: a
  running-ritual lock plus a 30s cooldown.

Requirements are checked twice — once at the emote gate, once inside the ritual at
the moment of payment. Every enlightenment is logged with the character, guid, and
the level it came from.

## Couplings with other Aeshnidae mods

**Aeshnidae.XpCurrency.** While retail zeroed the unspent xp pool, a player could
`/xp` it to a friend, enlighten, and have it sent back — the transfer dodged a cost.
`KeepUnassignedExperience: true` closes that hole by removing the incentive rather
than by patching XpCurrency: the pool survives by design, so there is nothing to
dodge. Only *unspent* xp survives; everything sunk into levels, attributes and ranks
is still destroyed, which is the bulk of a 275's lifetime earnings. Turning the
setting off reopens the hole, and `Mod.WarnOnCouplings` logs a warning at startup if
you do.

**Aeshnidae.SkillMastery.** Mastery ranks survive enlightenment because they live in
a shard table, not in the skill — `ResetSkill`, which this mod runs every skill
through, zeroes `Ranks`, `InitLevel` and `ExperienceSpent`. Nothing here tries to
preserve them, and nothing here may ever write into a `CreatureSkill` to preserve
anything: mastery survives by construction, and a special case would break it.

The balance coupling matters more than the mechanical one. Mastery survives and
retail ranks do not, so mastery priced below the retail rank it competes with makes
everything on this page toothless — the optimal play becomes buying nothing but
mastery and keeping the whole build through the reset. That invariant is
SkillMastery's to hold, in its cost bands.

**Aeshnidae.MaxLevel.** Skill credits past 275 are granted per level from
`CharacterLevelSkillCreditList`, so they come back with the levels. The credit
recompute here only restores the permanent sources (heritage and the three quests).

## Commands

```
/enlighten                progress towards enlightenment, and what it would cost
/enlightenment-rules      the rules currently in force        (Advocate)
/enlighten-prep [player]  TESTING: level, auras, society master - not enlightenment (Admin)
/enlightenment-reload     restart the mod, re-reading settings (Admin)
```

## Files

| File | Role |
| --- | --- |
| `Settings.cs` | Every rule, one field per clause. Read this first. |
| `Requirements.cs` | The eligibility gate, and the only place that decides it. |
| `Ritual.cs` | What is taken, left alone, and given. |
| `Guard.cs` | Re-entry lock and cooldown. |
| `Patches.cs` | The three interceptions, and why the emote gate is one of them. |
| `Commands.cs` | Read-only, on purpose. |
