namespace Aeshnidae.ClearWeenie;

/// <summary>
/// Entry point ACE looks for.
///
/// ModContainer resolves the type purely by convention:
///     assembly "Aeshnidae.ClearWeenie.dll"  ->  type "Aeshnidae.ClearWeenie.Mod"
/// so this class must be named <c>Mod</c> and live in a namespace matching the
/// assembly name. Renaming one without the other is the most common reason a mod
/// loads its assembly and then logs "Missing IHarmonyMod Type".
/// </summary>
public class Mod : IHarmonyMod
{
    /// <summary>
    /// "Aeshnidae.ClearWeenie". Taken from the assembly rather than written out, so a
    /// rename cannot leave a stale literal behind.
    /// (Note: nameof(Aeshnidae.ClearWeenie) would yield just "Template" - nameof on a
    /// namespace returns only its last segment.)
    /// </summary>
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    /// <summary>Harmony owner id. Unique per mod - it is what UnpatchAll targets.</summary>
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";

    /// <summary>Folder this mod was loaded from, e.g. C:\ACEPublic\Mods\Aeshnidae.ClearWeenie</summary>
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    /// <summary>Live settings, re-read every time the mod is enabled.</summary>
    public static Settings Settings { get; private set; } = new();

    private static Harmony? _harmony;
    private bool _disposed;

    /// <summary>
    /// Called by ModContainer.Enable() once the assembly is loaded.
    /// The world is not open yet at this point, so keep this cheap and defensive -
    /// anything that throws leaves the mod stuck Inactive.
    /// </summary>
    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        _harmony = new Harmony(HarmonyId);

        // Applies [HarmonyPatch] classes that are NOT in a [HarmonyPatchCategory].
        // Categorized patches stay dormant until you call _harmony.PatchCategory(...)
        // yourself - a convenient switch for optional features.
        // Pass the assembly explicitly. The parameterless overload finds its target
        // by walking the stack, and the JIT inlines a small Initialize(), so the walk
        // lands on ACE.Server instead of this mod and silently patches nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        ModManager.Log($"[{Name}] initialized from {ModPath}");
    }

    /// <summary>
    /// Called by ModContainer.Disable() on shutdown, hot-reload, and /mod disable.
    /// Every patch and event handler added above must come off here, or the old
    /// assembly cannot be collected and the next load will double-patch.
    /// </summary>
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
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
