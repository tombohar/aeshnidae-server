namespace Aeshnidae.DevKit;

/// <summary>
/// Entry point ACE looks for.
///
/// ModContainer resolves the type purely by convention:
///     assembly "Aeshnidae.DevKit.dll"  ->  type "Aeshnidae.DevKit.Mod"
/// so this class must be named <c>Mod</c> and live in a namespace matching the
/// assembly name. Renaming one without the other is the most common reason a mod
/// loads its assembly and then logs "Missing IHarmonyMod Type".
/// </summary>
public class Mod : IHarmonyMod
{
    /// <summary>
    /// "Aeshnidae.DevKit". Taken from the assembly rather than written out, so a
    /// rename cannot leave a stale literal behind.
    /// (Note: nameof(Aeshnidae.DevKit) would yield just "Template" - nameof on a
    /// namespace returns only its last segment.)
    /// </summary>
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    /// <summary>Folder this mod was loaded from, e.g. C:\ACEPublic\Mods\Aeshnidae.DevKit</summary>
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    /// <summary>Live settings, re-read every time the mod is enabled.</summary>
    public static Settings Settings { get; private set; } = new();

    private bool _disposed;

    /// <summary>
    /// Called by ModContainer.Enable() once the assembly is loaded.
    /// The world is not open yet at this point, so keep this cheap and defensive -
    /// anything that throws leaves the mod stuck Inactive.
    /// </summary>
    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        // No Harmony patches: this mod is commands only. It reads ACE's caches and the
        // world database, and writes to the database only through /wimport, and only
        // when Settings.AllowImport says this is a server where that is allowed.
        try
        {
            Db.Initialize();
        }
        catch (Exception ex)
        {
            // Not fatal: /winfo and /wexport work from ACE's own cache. Only the
            // diff/submit commands need the raw connection, and they report this.
            ModManager.Log($"[{Name}] world database connection not available: {ex.Message}", ModManager.LogLevel.Warn);
        }

        ModManager.Log($"[{Name}] initialized from {ModPath} - {(Settings.AllowImport ? "imports ALLOWED" : "imports off")}, " +
                       $"base schema {(Settings.HasBase ? Settings.BaseSchema : "(none)")}, label '{Settings.Label}'");
    }

    /// <summary>
    /// Called by ModContainer.Disable() on shutdown, hot-reload, and /mod disable.
    /// Nothing was patched, so there is nothing to unpatch; the caches are dropped so
    /// the old assembly can be collected.
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
            Exporter.Reset();
            Names.Reset();

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
