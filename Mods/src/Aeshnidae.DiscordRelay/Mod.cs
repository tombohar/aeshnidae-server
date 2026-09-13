namespace Aeshnidae.DiscordRelay;

/// <summary>
/// Entry point ACE looks for.
///
/// ModContainer resolves the type purely by convention:
///     assembly "Aeshnidae.DiscordRelay.dll"  ->  type "Aeshnidae.DiscordRelay.Mod"
/// so this class must be named <c>Mod</c> and live in a namespace matching the
/// assembly name. Renaming one without the other is the most common reason a mod
/// loads its assembly and then logs "Missing IHarmonyMod Type".
/// </summary>
public class Mod : IHarmonyMod
{
    /// <summary>
    /// "Aeshnidae.DiscordRelay". Taken from the assembly rather than written out, so a
    /// rename cannot leave a stale literal behind.
    /// (Note: nameof(Aeshnidae.DiscordRelay) would yield just "DiscordRelay" - nameof on
    /// a namespace returns only its last segment.)
    /// </summary>
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;

    /// <summary>Harmony owner id. Unique per mod - it is what UnpatchAll targets.</summary>
    public static readonly string HarmonyId = $"mod.{Name.ToLowerInvariant()}";

    /// <summary>Folder this mod was loaded from, e.g. C:\ACEPublic\Mods\Aeshnidae.DiscordRelay</summary>
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    /// <summary>Live settings, re-read every time the mod is enabled.</summary>
    public static Settings Settings { get; private set; } = new();

    /// <summary>
    /// The running bridge, or null if the mod is disabled or the chat hook could not be
    /// found. The patch null-checks this, so a missing relay is simply a quiet no-op.
    /// </summary>
    internal static ChatRelay? Relay { get; private set; }

    private static Harmony? _harmony;
    private bool _disposed;

    /// <summary>
    /// Called by ModContainer.Enable() once the assembly is loaded.
    /// The world is not open yet at this point, so keep this cheap and defensive -
    /// anything that throws leaves the mod stuck Inactive.
    /// </summary>
    public void Initialize()
    {
        Settings = Settings.Load(ModPath);

        // Resolve the target before patching. It is a private ACE method referenced by
        // name, so an upstream rename would otherwise take the whole mod down with a
        // Harmony exception out of Initialize.
        if (AccessTools.Method(typeof(TurbineChatHandler), Patches.LogTurbineChat) is null)
        {
            ModManager.Log($"[{Name}] TurbineChatHandler.{Patches.LogTurbineChat} not found - " +
                           "ACE's chat handler has changed shape. Nothing will be relayed.",
                           ModManager.LogLevel.Error);
            return;
        }

        _harmony = new Harmony(HarmonyId);

        // Applies [HarmonyPatch] classes that are NOT in a [HarmonyPatchCategory].
        // Pass the assembly explicitly. The parameterless overload finds its target
        // by walking the stack, and the JIT inlines a small Initialize(), so the walk
        // lands on ACE.Server instead of this mod and silently patches nothing.
        _harmony.PatchAllUncategorized(typeof(Mod).Assembly);

        Relay = new ChatRelay(Settings);

        ModManager.Log($"[{Name}] initialized from {ModPath}; relaying {Describe()}");
    }

    /// <summary>Which channels are actually wired up, for the startup log line.</summary>
    private static string Describe()
    {
        if (!Settings.Enabled)
            return "nothing (Enabled: false)";

        var live = Settings.Channels
            .Where(c => c.Value is { Enabled: true } && !string.IsNullOrWhiteSpace(c.Value.WebhookUrl))
            .Select(c => c.Key)
            .ToList();

        return live.Count > 0
            ? string.Join(", ", live)
            : "nothing yet - add webhook URLs to Settings.json, then /discordrelay-reload";
    }

    /// <summary>
    /// Called by ModContainer.Disable() on shutdown, hot-reload, and /mod disable.
    /// Every patch and background task added above must come off here, or the old
    /// assembly cannot be collected and the next load will double-patch.
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
            // Unpatch first: no new chat can arrive while the relay drains what it has.
            _harmony?.UnpatchAll(HarmonyId);
            _harmony = null;

            Relay?.Dispose();
            Relay = null;

            ModManager.Log($"[{Name}] shut down");
        }

        _disposed = true;
    }
}
