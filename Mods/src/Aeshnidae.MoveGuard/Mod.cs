namespace Aeshnidae.MoveGuard;

/// <summary>
/// Entry point ACE looks for: assembly "Aeshnidae.MoveGuard.dll" -> type
/// "Aeshnidae.MoveGuard.Mod".
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";

    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    public static Settings Settings { get; private set; } = new();

    /// <summary>Counters for /moveguard, so the rule is visible rather than mysterious.</summary>
    public static long SpeedViolations, GeometryViolations, Rejections, Kicks;

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        try
        {
            Settings = Settings.Load(ModPath);

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

            ModManager.Log($"[{Name}] active - mode {Settings.ModeValue}, " +
                           $"speed tolerance x{Settings.SpeedTolerance:F2} + {Settings.SlackUnits:F1}u, " +
                           $"catch-up {Settings.MaxCatchUpSeconds:F1}s, " +
                           $"geometry {(Settings.CheckGeometry ? (Settings.EnforceGeometry ? "enforced" : "logged") : "off")}, " +
                           $"exempt {Settings.ExemptLevel}+");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] failed to initialize: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>Re-read Settings.json without a restart. Patches stay in place.</summary>
    public static void Reload()
    {
        Settings = Settings.Load(ModPath);
        ModManager.Log($"[{Name}] settings reloaded - mode {Settings.ModeValue}");
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
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            Tracker.Clear();

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
