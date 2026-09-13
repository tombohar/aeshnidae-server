# Aeshnidae.ClearWeenie

Reloads one weenie from the world database and refreshes its live instances. The
per-weenie alternative to `/clearcache`.

```
/clearweenie 22642              one NPC
/clearweenie 22642 3930         several - the wcids from a change file's header
```

Developer access. Reports what it did:

```
22642 Brighteyes, the Tailor: reloaded, 1 live object(s) refreshed across 37 loaded landblock(s).
```

## Why not /clearcache

`/clearcache` drops **every** cache on the shard - weenies, landblock instances,
recipes, spells, wielded treasure. The next touch of anything is a MySQL round trip,
and on a busy server that is a wall of round trips inside the tick loop. That is the
lag spike everyone feels. Editing one NPC does not need the drudge weenie reloaded.

## What it does

1. `ClearCachedWeenie(wcid)` - ACE's own surgical evict, the one `/import-sql` uses.
2. `GetCachedWeenie(wcid)` - reload from the database now, rather than making the
   next spawn pay for it.
3. Walk every loaded landblock and re-point each live object of that wcid at the
   fresh weenie's collections.

Step 3 is the part `/import-sql` is missing, and it matters. A spawned NPC does not
get a private copy of everything: the collections ACE considers "typically not
modified" - **emotes**, create lists, event filters - are shared **by reference** with
the cached weenie. So Brighteyes standing in town is answering straight out of the
cache entry, and evicting the entry does not touch her. She keeps a reference to the
old collection until she is despawned. Re-pointing is what makes an emote edit live on
the next hand-in rather than the next server restart.

Each re-point is a single reference write, which is atomic in .NET. A landblock thread
mid-iteration finishes the old collection; the next lookup gets the new one.

## What it deliberately does not refresh

| | Why |
| --- | --- |
| Stats, attributes, skills, spell books | Cloned into each object at spawn. Nobody wants a living monster's attributes to change mid-fight; they take effect on the next spawn, which is the expectation anyway. |
| Generator profiles | A generator snapshots its `PropertiesGenerator` into `GeneratorProfile` objects at init, so re-pointing the biota's list changes nothing it reads. Generator edits need `/reload-landblock`. |
| Items in inventories or on the ground | Same as stock ACE. An item is what it was when it was made. |
| Objects inside instanced-dungeon copies | Copies (Aeshnidae.InstancesNoDat) are not in `LandblockManager`'s table. They are short-lived by design; the next copy opened spawns from the fresh weenie. |

## Where it fits

`Content\sql\changes\` - the apply script prints the exact `/clearweenie` line for the
wcids it just touched, and the same list lands in the Discord notification.
