namespace Aeshnidae.Bank;

/// <summary>
/// A per-account bank for pyreals, luminance and keys.
///
/// Balances live in their own table in the shard database (see BankDb), keyed by
/// account id so every character on an account shares one balance. Nothing in
/// ACE's own source or schema is modified.
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

        // The HUD listening flag must be ephemeral before anyone sets it; see HudFeed.
        HudFeed.RegisterProperty();

        _harmony = new Harmony(HarmonyId);
        // Pass the assembly explicitly. The parameterless overload finds its target
        // by walking the stack, and the JIT inlines a small Initialize(), so the walk
        // lands on ACE.Server instead of this mod and silently patches nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        // ModManager.Initialize runs at Program.cs:158, before DatabaseManager is
        // started at line 259 - but ConfigManager is already up at line 154, and
        // that is all BankDb needs to open its own connection.
        try
        {
            BankDb.Initialize();

            // Only after the table exists - the flush writes to it.
            Earning.Start();

            // A reload (/bankreload, hot reload) rebuilds this assembly's statics while
            // players stay online, and the auto-bank flags and /earned session clocks
            // are only read at login. Re-read them for everyone already in the world -
            // otherwise a flagged character's luminance lands on them, and past their
            // cap is lost, until they relog.
            foreach (var online in PlayerManager.GetAllOnline())
            {
                AutoBank.Load(online);
                History.StartSession(online);
            }

            ModManager.Log($"[{Name}] ready - balances in `{BankDb.TableName}` on the shard database");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Name}] could not open bank storage, /bank will refuse to run: {ex.Message}",
                           ModManager.LogLevel.Error);
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
            // Unpatch FIRST so nothing can earn into the buffer while it is being
            // drained, then flush what is already there. The other order can lose an
            // award that lands between the flush and the unpatch.
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            Earning.Stop();

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
