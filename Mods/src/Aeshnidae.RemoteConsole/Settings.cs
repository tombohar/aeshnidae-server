namespace Aeshnidae.RemoteConsole;

/// <summary>Settings.json in the deployed mod folder. Edit and /remoteconsole-reload.</summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// Where command files are dropped. Relative paths are under the mod folder.
    ///
    /// Whoever can write here can run any console command, so this directory IS the
    /// permission boundary. It should be owned by the account the server runs as and
    /// reachable only over ssh - which is exactly the boundary that already exists.
    /// </summary>
    public string InboxDirectory { get; set; } = "inbox";

    /// <summary>How often the inbox is checked, in seconds.</summary>
    public double PollSeconds { get; set; } = 1.0;

    /// <summary>Delete .done receipts older than this many minutes. 0 keeps them forever.</summary>
    public int ReceiptLifetimeMinutes { get; set; } = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

            var defaults = new Settings();
            defaults.Save(modPath);
            return defaults;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not read {path}, using defaults: {ex.Message}", ModManager.LogLevel.Warn);
            return new Settings();
        }
    }

    public void Save(string modPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(modPath, FileName), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not save settings: {ex.Message}", ModManager.LogLevel.Error);
        }
    }
}
