namespace Aeshnidae.Hud;

/// <summary>
/// The HUD feed: server-described panels for clients that can draw them.
///
/// This mod owns the switch (/hud) and the fan-out to the mods that own panels; the
/// panels themselves live with their numbers (Aeshnidae.SkillMastery, Aeshnidae.Bank).
/// No patches - it is a command and one ephemeral flag. See Feed.cs for the contract.
/// </summary>
public class Mod : IHarmonyMod
{
    public static readonly string Name = typeof(Mod).Assembly.GetName().Name!;
    public static readonly string ModPath = Path.Combine(ModManager.ModPath, Name);

    public static ModContainer? Container => ModManager.GetModContainerByPath(ModPath);

    private bool _disposed;

    public void Initialize()
    {
        // Before any /hud on can run: the flag must be ephemeral before it is first set.
        Feed.RegisterProperty();

        ModManager.Log($"[{Name}] ready - /hud on marks a session listening, providers are /{Feed.ProviderPrefix}*");
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
            ModManager.Log($"[{Name}] shut down");

        _disposed = true;
    }
}
