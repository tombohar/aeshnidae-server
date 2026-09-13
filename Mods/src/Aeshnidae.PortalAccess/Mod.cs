namespace Aeshnidae.PortalAccess;

/// <summary>
/// Portals stop caring what level you are.
///
/// Written alongside Aeshnidae.Enlightenment, and the two are related: the old
/// enlightenment exploit was worth running because dungeon portals had minimum
/// levels to dodge. Enlightenment closes the mechanism; this removes the prize.
/// They are separate mods because they are separate decisions - disabling one
/// should not silently change the other.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    public static Settings Settings { get; private set; } = new();

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        _harmony = new Harmony(HarmonyId);

        // Pass the assembly explicitly. The parameterless overload finds its target
        // by walking the stack, and the JIT inlines a small Initialize(), so the walk
        // lands on ACE.Server instead of this mod and silently patches nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        ModManager.Log($"[{Name}] active - portal minimum level {State(Settings.RemoveMinLevel)}, " +
                       $"maximum level {State(Settings.RemoveMaxLevel)}");
    }

    private static string State(bool removed) => removed ? "removed" : "enforced";

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

            ModManager.Log($"[{Name}] shut down, portal level requirements back in force");
        }

        _disposed = true;
    }
}
