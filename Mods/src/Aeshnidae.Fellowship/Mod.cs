namespace Aeshnidae.Fellowship;

/// <summary>
/// Fellowship size and experience sharing as settings rather than constants.
///
/// Size needs no patch - ACE keeps MaxFellows in a public static field. Sharing does,
/// because ACE's table is a switch that stops at 9 and falls through to a full share,
/// so the two have to move together. See Settings.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static Settings Settings { get; private set; } = new();

    /// <summary>ACE's own value, kept so unloading puts it back.</summary>
    private static int _originalMaxFellows;

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        if (Settings.EnsureSharePercentages())
            Settings.Save(ModPath);

        _originalMaxFellows = AceFellowship.MaxFellows;

        Apply();

        _harmony = new Harmony(HarmonyId);
        // Explicit assembly: the parameterless overload walks the stack, and an
        // inlined Initialize() makes it resolve to ACE.Server and patch nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        ModManager.Log($"[{Name}] fellowships up to {AceFellowship.MaxFellows} " +
                       $"(retail {_originalMaxFellows}), share at that size " +
                       $"{Settings.ShareFor(AceFellowship.MaxFellows):P0} each");
    }

    /// <summary>Pushes the configured size into ACE. Safe to call again after a reload.</summary>
    public static void Apply()
    {
        if (!Settings.Enabled)
        {
            AceFellowship.MaxFellows = _originalMaxFellows;
            return;
        }

        if (Settings.MaxFellows < 1)
        {
            ModManager.Log($"[{Name}] MaxFellows of {Settings.MaxFellows} makes no sense; " +
                           $"leaving it at {_originalMaxFellows}", ModManager.LogLevel.Warn);
            return;
        }

        AceFellowship.MaxFellows = Settings.MaxFellows;

        // A size with no share entry would fall to the floor, which is legal but almost
        // certainly not intended - say so rather than letting it surprise someone.
        for (var count = 1; count <= Settings.MaxFellows; count++)
        {
            if (!Settings.SharePercentages.ContainsKey(count))
            {
                ModManager.Log($"[{Name}] no share configured for {count} member(s); " +
                               $"they will get the {Settings.ShareBeyondTable:P0} floor",
                               ModManager.LogLevel.Warn);
            }
        }
    }

    /// <summary>Re-reads Settings.json and re-applies it, for /fellow reload.</summary>
    public static void Reload()
    {
        Settings = Settings.Load(ModPath);
        Settings.EnsureSharePercentages();
        Apply();
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
            // Put ACE's constant back; the field is global and outlives this mod.
            if (_originalMaxFellows > 0)
                AceFellowship.MaxFellows = _originalMaxFellows;

            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            ModManager.Log($"[{Name}] shut down, fellowship size back to {_originalMaxFellows}");
        }

        _disposed = true;
    }
}
