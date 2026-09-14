namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Dungeon instances that keep the source landblock id, so no client dat is ever
/// touched. See InstanceWorld for why that is possible, and Context for the one
/// question the design turns on.
///
/// Experimental. Off by default.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static Settings Settings { get; private set; } = new();

    /// <summary>
    /// Parsed instanced-dungeon config, by landblock. Cached because the entry hooks
    /// consult it on every teleport on the server, not just instanced ones.
    /// </summary>
    public static Dictionary<ushort, InstancedLandblock> Instanced { get; private set; } = new();

    public static bool IsInstanced(ushort landblock) => Instanced.ContainsKey(landblock);

    public static InstanceScope ScopeFor(ushort landblock) =>
        Instanced.TryGetValue(landblock, out var entry) ? entry.Scope : InstanceScope.Personal;

    public static string NameFor(ushort landblock) =>
        Instanced.TryGetValue(landblock, out var entry) && !string.IsNullOrWhiteSpace(entry.Name)
            ? entry.Name
            : $"{landblock:X4}";

    /// <summary>
    /// Re-reads the instanced dungeon list.
    ///
    /// Deliberately does NOT use ACE's Player.NoLog_Landblocks, which moves anyone who
    /// logged out there to their lifestone before they are placed. That was one answer
    /// to "these dungeons have no public version", but handing them a fresh copy is the
    /// better one - you come back where you were rather than across the world. It also
    /// would not have worked for the people most likely to test it: HandleNoLogLandblock
    /// returns early for Admin and Sentinel characters.
    /// </summary>
    /// <summary>
    /// Staff are never handed a copy automatically - not on login, not on walking in,
    /// not after a reload. An admin in an instanced dungeon is usually authoring it, and
    /// the master is the only version that can be authored, so quietly moving them into
    /// a private copy would break the one workflow that matters and look like the edit
    /// simply failed. /inst enter is how staff ask for a copy on purpose.
    /// </summary>
    public static bool IsStaff(Player player) =>
        (player.Session?.AccessLevel ?? AccessLevel.Player) >= AccessLevel.Admin;

    /// <summary>A routing trace line, when Settings.LogRouting is on.</summary>
    public static void Trace(string message)
    {
        if (Settings.LogRouting)
            ModManager.Log($"[{Name}] {message}");
    }

    public static void ReloadInstanced() => Instanced = Settings.InstancedMap();

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        Settings = Settings.Load(ModPath);
        ReloadInstanced();

        _harmony = new Harmony(HarmonyId);
        // Explicit assembly: the parameterless overload walks the stack, and an
        // inlined Initialize() makes it resolve to ACE.Server and patch nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        ModManager.Log($"[{Name}] loaded - {(Settings.Enabled ? "ENABLED" : "disabled")}, " +
                       $"up to {Settings.MaxCopiesPerLandblock} copies per landblock, " +
                       $"{Instanced.Count} instanced dungeon(s)" +
                       $"{(Settings.InteriorsOnly ? ", interiors only" : ", INTERIORS-ONLY RAIL IS OFF")}");
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            try
            {
                // Copies exist only in this mod's memory; leaving them behind would
                // strand anyone standing in one. Players are left where they stand
                // rather than recalled: the incoming assembly's first tick restores
                // them to a fresh copy, and being briefly in a drained master beats a
                // trip to the lifestone every time the mod is rebuilt.
                InstanceWorld.Clear(ejectPlayers: false);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Name}] failed to close instances on shutdown: {ex}",
                               ModManager.LogLevel.Error);
            }

            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
