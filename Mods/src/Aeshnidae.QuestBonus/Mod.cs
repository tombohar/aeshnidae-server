namespace Aeshnidae.QuestBonus;

/// <summary>
/// Quest Bonus. Solved quests accumulate "quest points" on the character, which
/// convert into a small permanent XP multiplier.
///
/// Ported from Aquafir's ACE.BaseMod sample (Samples/QuestBonus), with its
/// ACE.Shared dependency removed - the only pieces it actually needed were the
/// one-line HasSolves() extension and the FakeFloat.QuestBonus property id, both
/// now in QuestBonusExtensions.
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

        // At server startup this is empty. On a /mod restart it resyncs everyone
        // who is already logged in, so retuning Settings.json takes effect at once.
        ResyncOnlinePlayers();

        ModManager.Log($"[{Name}] enabled: {Settings.BonusConversion * 100:0.###}% XP per quest point" +
                       (Settings.MaxMultiplier > 0 ? $", capped at {Settings.MaxMultiplier:0.##}x" : ", uncapped"));
    }

    /// <summary>Recalculates every online character's total from their quest registry.</summary>
    public static int ResyncOnlinePlayers()
    {
        var count = 0;

        foreach (var player in PlayerManager.GetAllOnline())
        {
            try
            {
                player.ResyncQuestPoints();
                count++;
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Name}] resync failed for {player?.Name}: {ex.Message}", ModManager.LogLevel.Warn);
            }
        }

        return count;
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
