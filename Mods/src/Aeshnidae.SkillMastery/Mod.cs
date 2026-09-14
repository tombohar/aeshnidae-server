namespace Aeshnidae.SkillMastery;

/// <summary>
/// Skill progression past the retail cap, kept deliberately separate from retail
/// ranks so that enlightenment resets one and not the other.
///
/// See Mastery for why the bonus rides on InitLevel, and MasteryDb for why it is
/// stored outside the skill.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static Settings Settings { get; private set; } = new();

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        // The HUD listening flag must be ephemeral before anyone sets it; see HudFeed.
        HudFeed.RegisterProperty();

        // First run writes out the full band table so every segment is there to edit.
        if (Settings.EnsureBands())
            Settings.Save(ModPath);

        MasteryDb.Initialize();

        _harmony = new Harmony(HarmonyId);
        // Explicit assembly: the parameterless overload walks the stack, and an
        // inlined Initialize() makes it resolve to ACE.Server and patch nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        ModManager.Log($"[{Name}] ready - {Settings.RanksPerSkillPoint} ranks per skill point, " +
                       $"{Settings.Bands.Count} cost bands up to {Settings.MaxSkillPoints:N0} points" +
                       $"{(MasteryDb.Ready ? "" : " (storage unavailable, mastery disabled)")}");
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

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
