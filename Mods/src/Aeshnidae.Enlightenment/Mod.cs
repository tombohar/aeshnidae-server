namespace Aeshnidae.Enlightenment;

/// <summary>
/// Aeshnidae's terms for enlightenment.
///
/// Retail's bargain was all cost: reset to level 1 and lose society, luminance,
/// auras, aetheria and your unspent xp, for +1 to all skills, +2 vitality and a
/// title. Aeshnidae keeps the reset - which is the part that makes it mean
/// something - and stops charging for the grinds you have already finished. The
/// full terms are in Settings.cs, one field per clause.
///
/// Entry is through the Font of Enlightenment and Rebirth (wcid 53412) and nothing
/// else. See Commands.cs for why this mod adds no way to grant one, and Guard.cs
/// for what stops the Font granting two.
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

        WarnOnCouplings();

        ModManager.Log($"[{Name}] active - level {Settings.BaseLevelRequirement} " +
                       $"+{Settings.LevelRequirementPerEnlightenment} per enlightenment, " +
                       $"{(Settings.MaxEnlightenments > 0 ? $"max {Settings.MaxEnlightenments}" : "uncapped")}");
    }

    /// <summary>
    /// Shouts about the two settings combinations that break something in a mod
    /// other than this one. Both are legitimate choices; neither is one you want to
    /// discover from a player.
    /// </summary>
    public static void WarnOnCouplings()
    {
        if (!Settings.Enabled)
            return;

        // Aeshnidae.XpCurrency lets a player move unspent xp to another character.
        // While enlightenment zeroed that pool, the transfer was a way to carry it
        // across the reset - park it with a friend, enlighten, take it back.
        // KeepUnassignedExperience closes that by making the dodge pointless.
        // Turning it off reopens it, and XpCurrency has no guard of its own.
        if (!Settings.KeepUnassignedExperience && IsLoaded("Aeshnidae.XpCurrency"))
        {
            ModManager.Log(
                $"[{Name}] KeepUnassignedExperience is off while Aeshnidae.XpCurrency is loaded. " +
                "A player can transfer their unspent xp to another character, enlighten, and have it " +
                "sent back - the reset is avoidable. Either keep the pool, or add a transfer lockout " +
                "to XpCurrency.",
                ModManager.LogLevel.Warn);
        }

        // Aeshnidae.SkillMastery ranks survive enlightenment by design, because they
        // live outside the skill. That is only balanced while mastery costs more
        // than the retail rank it competes with - otherwise the optimal play is to
        // buy nothing but mastery and keep the entire build through the reset.
        if (Settings.ResetSkills && IsLoaded("Aeshnidae.SkillMastery"))
        {
            ModManager.Log(
                $"[{Name}] Aeshnidae.SkillMastery is loaded: mastery ranks survive enlightenment and " +
                "retail ranks do not. Keep its cost bands above the retail rank they compete with, or " +
                "enlightenment stops costing anything.");
        }
    }

    private static bool IsLoaded(string modName)
    {
        try
        {
            return ModManager.GetModContainerByPath(Path.Combine(ModManager.ModPath, modName))?.Status
                   == ModStatus.Active;
        }
        catch
        {
            return false;
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
            Guard.Clear();

            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            ModManager.Log($"[{Name}] shut down, retail enlightenment rules back in force");
        }

        _disposed = true;
    }
}
