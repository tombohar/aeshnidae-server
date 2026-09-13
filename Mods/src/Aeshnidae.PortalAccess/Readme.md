# Aeshnidae.PortalAccess

Portals stop caring what level you are.

## What it does

Two postfixes on `WorldObject.MinLevel` and `WorldObject.MaxLevel`, each returning
`null` when the object is a `Portal`. `Portal.CheckUseRequirements` then runs in
full and finds no level requirement to enforce.

In the current world database, out of 3,306 portal weenies:

| | weenies | range |
| --- | --- | --- |
| `MinLevel` | 933 (907 above 1) | 1 – 275 |
| `MaxLevel` | 127 | 5 – 150 |

Weenie counts overstate it. Only 706 of the gated weenies are actually placed,
as **865 physical portals** in landblocks.

And a large share of those are **exits, not entrances**. The biggest single cluster
is 134 placed portals at `MinLevel 180`, of which 37 are named "Surface"; the top
ten also includes "Surface Portal", "Temple Exit" and several more "Surface"
variants. Dungeon exit portals inherit the entrance's minimum, so each gated dungeon
contributes two or more gated portals, and multi-level dungeons contribute more —
865 portals is perhaps half that many distinct gated dungeons.

Worth knowing when judging the blast radius: for exit portals, removing the minimum
is a fix rather than a relaxation. An under-level character currently cannot use the
front door out.

Both halves do real work here, and they are different decisions — see the two
settings below.

## Why not the obvious patch

The tempting version is a postfix on `Portal.CheckUseRequirements` that turns
`YouAreNotPowerfulEnoughToUsePortal` into success. That is wrong, and quietly so.
The level test sits near the top of that method and returns early, so converting
its failure into a success also skips everything below it — PK and Olthoi
restrictions, vitae, account age, quest requirements. A portal that was both level
gated and PK gated would silently stop being PK gated.

Removing the requirement instead of overriding the refusal leaves every other check
running exactly as before.

## Why the type test

`MinLevel` and `MaxLevel` are declared on `WorldObject`, not `Portal`, and housing
shares them — a slumlord's `MinLevel` is the level needed to buy the dwelling.

This is not hypothetical: **45 SlumLord weenies** in the current world database set
`MinLevel`. Without the `is Portal` test this mod would have quietly removed the
level requirement to buy a house.

## Settings

| setting | default | effect |
| --- | --- | --- |
| `RemoveMinLevel` | `true` | 933 portals stop requiring a minimum level. This is the half that matters for the enlightenment exploit. |
| `RemoveMaxLevel` | `true` | 127 portals stop *capping* level — this opens low-level-only content to maxed characters, which is a content decision, not an access one. Set `false` to keep newbie dungeons closed to 275s. |

ACE has its own switch for the maximum (the server property
`use_portal_max_level_requirement`). This setting overrides it for portals in either
direction, so set both consistently or leave the property alone.

## Relationship to Aeshnidae.Enlightenment

The two were written together. Players used to enlighten in portal space so the old
`/enlighten` command's lifestone teleport could not land, keeping a level 1
character inside high-tier content and re-levelling off mobs far above them.

Enlightenment closes the mechanism (`RequireProximityToNpc` — the grant refuses to
fire unless the player is still standing at the Font). This removes the prize: with
no minimum level to dodge, there is nothing to gain by dodging it.

They are separate mods because they are separate decisions. Disabling one should not
silently change the other.

## Commands

```
/portalaccess           what is currently suppressed   (Advocate)
/portalaccess-reload    restart, re-reading settings   (Admin)
```
