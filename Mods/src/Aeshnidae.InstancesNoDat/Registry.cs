namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// A per-copy view of ACE's global physics object registry.
///
/// ServerObjectManager keeps one ConcurrentDictionary keyed by guid for the whole
/// server, and landblock instances have DETERMINISTIC guids - a drudge authored in
/// 002B is 0x7002B668 in the master and 0x7002B668 in every copy of it. So all of them
/// compete for a single slot, last write wins, and both the master and the copies
/// populate themselves on background tasks: which one ends up registered is a race.
///
/// Fourteen call sites read that registry - targeting, sticky projectiles, and the
/// voyeur checks that decide who gets told about whom - so losing the race does not
/// merely lose a lookup. It hands physics a different copy's object.
///
/// The fix is the same one this mod already uses for landblock and cell lookups: scope
/// the answer to the copy being processed. A guid is only ambiguous within one landblock
/// id, and only for static guids:
///
///     0x50000001-0x5FFFFFFF  players           unique, must stay global
///     0x70000000-0x7FFFFFFF  landblock instances   COLLIDE, one per copy
///     0x80000000+            corpses, loot, projectiles   unique, must stay global
///
/// So players and dynamics are left entirely alone - they are unique already, and
/// hiding a player from a global lookup would break far more than it fixed. Only static
/// guids are held here, and only for our copies. The master keeps the global slot,
/// which means a lookup with no ambient copy still answers exactly what it always did.
/// </summary>
public static class Registry
{
    private static readonly ConcurrentDictionary<(ushort Landblock, int Copy, uint Guid), PhysicsObj> _objects = new();

    public static int Count => _objects.Count;

    /// <summary>
    /// Whether this guid is one of the ambiguous ones. Anything else belongs in ACE's
    /// own registry and is none of our business.
    /// </summary>
    public static bool IsAmbiguous(uint guid) => ObjectGuid.IsStatic(guid);

    /// <summary>The copy currently being processed, if it is one of ours.</summary>
    private static (ushort Landblock, int Copy)? Scope()
    {
        var ambient = Context.Ambient;

        if (ambient is null)
            return null;

        var copy = InstanceWorld.CopyOf(ambient);

        if (copy <= 0)
            return null;   // the master, or a landblock we do not own

        return ((ushort)((ambient.Id.LandblockX << 8) | ambient.Id.LandblockY), copy);
    }

    /// <summary>
    /// Registers a copy's object here instead of globally. Returns false when the
    /// object is not ours to hold, in which case ACE's own registry should take it.
    /// </summary>
    public static bool Add(PhysicsObj obj)
    {
        if (obj is null || !IsAmbiguous(obj.ID))
            return false;

        if (Scope() is not { } scope)
            return false;

        _objects[(scope.Landblock, scope.Copy, obj.ID)] = obj;
        return true;
    }

    public static bool Remove(PhysicsObj obj)
    {
        if (obj is null || !IsAmbiguous(obj.ID))
            return false;

        if (Scope() is not { } scope)
            return false;

        _objects.TryRemove((scope.Landblock, scope.Copy, obj.ID), out _);
        return true;
    }

    /// <summary>
    /// The object for a guid within the copy being processed, or null to fall through
    /// to ACE's global answer.
    /// </summary>
    public static PhysicsObj? Get(uint guid)
    {
        if (!IsAmbiguous(guid))
            return null;

        if (Scope() is not { } scope)
            return null;

        return _objects.TryGetValue((scope.Landblock, scope.Copy, guid), out var obj) ? obj : null;
    }

    /// <summary>Drops everything a closed copy was holding.</summary>
    public static int Forget(ushort landblock, int copy)
    {
        var dropped = 0;

        foreach (var key in _objects.Keys.ToList())
        {
            if (key.Landblock == landblock && key.Copy == copy && _objects.TryRemove(key, out _))
                dropped++;
        }

        return dropped;
    }

    public static void ForgetAll() => _objects.Clear();
}
