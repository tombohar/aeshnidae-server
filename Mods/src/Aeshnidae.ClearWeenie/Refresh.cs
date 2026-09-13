namespace Aeshnidae.ClearWeenie;

/// <summary>
/// Evicts one weenie from ACE's cache and brings its live instances up to date.
///
/// Why a global /clearcache is the wrong tool for a one-weenie edit. It drops EVERY
/// cache at once - weenies, landblock instances, recipes, spells, wielded treasure -
/// so the next touch of anything on the shard is a MySQL round trip, and on a busy
/// server that is a wall of round trips inside the tick loop. That is the lag spike.
/// Editing Brighteyes does not need the drudge weenie reloaded.
///
/// ACE already has the surgical primitive, ClearCachedWeenie(wcid); its own
/// /import-sql uses it. This wraps it in a command and adds the half it is missing.
///
/// The missing half. A spawned NPC does not get a private copy of everything - the
/// collections ACE considers "typically not modified" are SHARED BY REFERENCE with the
/// cached weenie (WeenieConverter.ConvertToBiota, referenceWeenieCollectionsForCommon
/// Properties). PropertiesEmote is one of them. So the NPC standing in Holtburg is
/// running its dialogue straight out of the cache entry, and evicting that entry does
/// not touch her: she keeps a reference to the OLD collection and answers from it
/// until she is despawned. The next spawn would be right; the one players are talking
/// to would not. So after reloading, every live object of that wcid is re-pointed at
/// the fresh collections, and the change is live on the next hand-in.
///
/// What this deliberately does NOT refresh, and why:
///   - Cloned properties (stats, attributes, skills, spell books). Those were copied
///     into each object at spawn, and updating a living monster's attributes mid-fight
///     is not something anyone wants. They take effect on the next spawn, which is the
///     expectation anyway.
///   - Generator profiles. A generator snapshots PropertiesGenerator into its own
///     GeneratorProfile objects at init, so re-pointing the biota's list would change
///     nothing it reads. Generator edits need the landblock reloaded.
///   - Items already in inventories or on the ground. Same as stock ACE: an item is
///     what it was when it was made.
///   - Objects inside instanced-dungeon copies (Aeshnidae.InstancesNoDat). Copies are
///     not in LandblockManager's table, so they are not walked here. They are short
///     lived by design - the next copy opened spawns from the fresh weenie.
/// </summary>
public static class Refresh
{
    public record Result(uint Wcid, string Name, bool WasCached, int LiveRefreshed, int LandblocksWalked, string? Error);

    public static Result One(uint wcid)
    {
        var wasCached = DatabaseManager.World.ClearCachedWeenie(wcid);

        // Reload straight away rather than leaving the next spawn to pay for it. Also
        // the only way to find out whether the wcid exists at all - GetCachedWeenie
        // answers null for a wcid that is not in the database.
        var fresh = DatabaseManager.World.GetCachedWeenie(wcid);

        if (fresh is null)
            return new(wcid, "", wasCached, 0, 0, $"no weenie {wcid} in the world database");

        var name = fresh.PropertiesString is not null && fresh.PropertiesString.TryGetValue(PropertyString.Name, out var n)
            ? n
            : fresh.ClassName ?? "";

        var refreshed = 0;
        var walked = 0;

        foreach (var landblock in LandblockManager.GetLoadedLandblocks())
        {
            walked++;

            // A snapshot - the landblock keeps ticking on its own thread while this
            // walks, and ToList() inside is what makes that safe to iterate.
            foreach (var wo in landblock.GetAllWorldObjectsForDiagnostics())
            {
                if (wo.WeenieClassId != wcid || wo is Player)
                    continue;

                RePoint(wo, fresh);
                refreshed++;
            }
        }

        return new(wcid, name, wasCached, refreshed, walked, null);
    }

    /// <summary>
    /// Swaps the reference-shared collections on a live object for the fresh weenie's.
    ///
    /// Exactly the four ConvertToBiota shares when referenceWeenieCollectionsForCommon
    /// Properties is true, minus PropertiesGenerator (see the class comment). Each
    /// assignment is a single reference write, which is atomic in .NET - a landblock
    /// thread that already grabbed the old collection finishes iterating the old one,
    /// and the next lookup gets the new one. No lock, no torn state.
    /// </summary>
    private static void RePoint(WorldObject wo, Weenie fresh)
    {
        var biota = wo.Biota;

        biota.PropertiesEmote = fresh.PropertiesEmote;
        biota.PropertiesCreateList = fresh.PropertiesCreateList;
        biota.PropertiesEventFilter = fresh.PropertiesEventFilter;
    }
}
