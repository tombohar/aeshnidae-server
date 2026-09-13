namespace Aeshnidae.MaxLevel;

/// <summary>
/// Raises the character level cap beyond retail's 275 by extending the in-memory
/// XP table, continuing the retail curve's own growth law rather than inventing
/// a new one. See LevelTable for the maths.
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

        // Cold start: the dats are not loaded yet, so the patch above does the work.
        // Hot reload (/mod restart) on a running server: they already are, and
        // DatManager.Initialize will not fire again, so apply immediately.
        ApplyLevelCap("mod enabled");
    }

    /// <summary>Safe to call more than once - LevelTable.TryExtend is idempotent.</summary>
    public static void ApplyLevelCap(string reason)
    {
        try
        {
            var table = DatManager.PortalDat?.XpTable;

            if (table is null)
                return;   // cold start; the DatManager postfix will call us again

            if (LevelTable.TryExtend(table, Settings, out var message))
                ModManager.Log($"[{Name}] {message} ({reason})");
            else
                ModManager.Log($"[{Name}] not applied: {message}", ModManager.LogLevel.Warn);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] failed to extend the level table: {ex}", ModManager.LogLevel.Error);
        }
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
                // Put the shared dat object back exactly as it was.
                LevelTable.Revert(DatManager.PortalDat?.XpTable);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Name}] failed to revert the level table: {ex}", ModManager.LogLevel.Error);
            }

            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            ModManager.Log($"[{Name}] shut down, level cap back to {LevelTable.RetailMaxLevel}");
        }

        _disposed = true;
    }
}
