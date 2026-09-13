namespace Aeshnidae.AdminAudit;

/// <summary>
/// Entry point ACE looks for.
///
/// ModContainer resolves the type purely by convention:
///     assembly "Aeshnidae.AdminAudit.dll"  ->  type "Aeshnidae.AdminAudit.Mod"
/// so this class must be named <c>Mod</c> and live in a namespace matching the
/// assembly name.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    /// <summary>Harmony owner id. Unique per mod - it is what UnpatchAll targets.</summary>
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";

    /// <summary>Folder this mod was loaded from, e.g. C:\ACEPublic\Mods\Aeshnidae.AdminAudit</summary>
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    /// <summary>Live settings, re-read every time the mod is enabled.</summary>
    public static Settings Settings { get; private set; } = new();

    /// <summary>The sinks. Null while the mod is disabled; every patch null-checks it.</summary>
    internal static Auditor? Auditor { get; private set; }

    /// <summary>When this mod last started recording - the point before which the trail says nothing.</summary>
    public static DateTime StartedUtc { get; private set; }

    private static Harmony? _harmony;
    private bool _disposed;

    public void Initialize()
    {
        try
        {
            Settings = Settings.Load(ModPath);

            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

            Auditor = new Auditor(Settings, ModPath);
            StartedUtc = DateTime.UtcNow;

            if (Settings.AuditSiblingMods)
            {
                SiblingPatches.Apply(_harmony);
                ScheduleRebind();
            }

            var bound = SiblingPatches.Status.Count(h => h.Bound);

            ModManager.Log($"[{Name}] recording to {Auditor.Log.Directory}; " +
                           $"threshold {Settings.Threshold}, " +
                           $"Discord {(Auditor.Discord is null ? "off" : $"on ({Auditor.Sinks.Count} webhook{(Auditor.Sinks.Count == 1 ? "" : "s")})")}, " +
                           $"sibling hooks {bound}/{SiblingPatches.Status.Count}");

            // The trail should say when it started, so a gap is distinguishable from a
            // quiet period.
            Auditor.Record(new AuditEvent
            {
                Kind = nameof(AuditKind.Narrated),
                Actor = "server",
                Source = "server",
                Action = "audit-start",
                Outcome = "ok",
                Detail = $"audit started - threshold {Settings.Threshold}, {bound} sibling hook(s) bound",
            });
        }
        catch (Exception ex)
        {
            // A throw here leaves the mod Inactive, which would mean no audit at all.
            ModManager.Log($"[{Name}] failed to initialize: {ex}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Binds the sibling hooks again a little after startup.
    ///
    /// ModMetadata.Priority is a uint, so there is no value that reliably sorts this mod
    /// after the others - they are all 0, and the order within a priority is just
    /// whatever order the folders come back in. So load order cannot be relied on: the
    /// first Apply may find none of the sibling assemblies loaded yet.
    ///
    /// Rather than fight the ordering, bind again once everything has settled. Apply is
    /// idempotent, so the pass costs nothing when the first one already succeeded.
    /// </summary>
    private static void ScheduleRebind()
    {
        var harmony = _harmony;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(20));

                // The mod may have been disabled while we waited.
                if (!ReferenceEquals(_harmony, harmony) || harmony is null)
                    return;

                var before = SiblingPatches.Status.Count(h => h.Bound);

                SiblingPatches.Apply(harmony);

                var after = SiblingPatches.Status.Count(h => h.Bound);

                if (after != before)
                    ModManager.Log($"[{Name}] sibling hooks after startup settled: {after}/{SiblingPatches.Status.Count} bound");

                foreach (var hook in SiblingPatches.Status.Where(h => !h.Bound))
                    ModManager.Log($"[{Name}] not auditing {hook.Target} - {hook.Note}", ModManager.LogLevel.Warn);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Name}] deferred hook binding failed: {ex.Message}", ModManager.LogLevel.Warn);
            }
        });
    }

    /// <summary>
    /// Re-resolves the sibling-mod hooks. Needed after any of those mods reloads -
    /// /mod find reloads every mod, which silently unbinds them.
    /// </summary>
    public static int Rebind()
    {
        if (_harmony is null)
            return 0;

        SiblingPatches.Apply(_harmony);
        return SiblingPatches.Status.Count(h => h.Bound);
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
            // Say so in the trail before tearing anything down, so a deliberate
            // disable is visible rather than looking like an unexplained gap.
            Auditor?.Record(new AuditEvent
            {
                Kind = nameof(AuditKind.Narrated),
                Actor = "server",
                Source = "server",
                Action = "audit-stop",
                Outcome = "ok",
                Detail = "audit stopped",
            });

            // Unpatch first so nothing new arrives while the sinks drain.
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            Auditor?.Dispose();
            Auditor = null;

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
