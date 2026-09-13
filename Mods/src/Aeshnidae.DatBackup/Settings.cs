namespace Aeshnidae.DatBackup;

/// <summary>
/// Per-mod configuration, read from Settings.json in the deployed mod folder
/// (next to the dll, not in source). Written with defaults on first run, and
/// openable in-game with "/mod settings Aeshnidae.DatBackup".
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>Take a snapshot when the server starts.</summary>
    public bool SnapshotOnStartup { get; set; } = true;

    /// <summary>
    /// Where snapshots live. Empty means "DatBackup" beside this mod, which keeps a
    /// fresh install self-contained; point it at another drive if you would rather
    /// the backups not share a disk with the thing they are backing up.
    /// </summary>
    public string BackupDirectory { get; set; } = "";

    /// <summary>
    /// Folder of dats to snapshot. Empty means the server's own DatFilesDirectory
    /// from Config.js, which is the answer you want unless you are testing.
    /// </summary>
    public string DatDirectory { get; set; } = "";

    /// <summary>
    /// How many snapshots to keep. Because identical files are stored once, twenty
    /// snapshots of an unchanged dat set cost one copy plus twenty small manifests -
    /// so this can be generous without costing disk.
    /// </summary>
    public int KeepSnapshots { get; set; } = 20;

    /// <summary>
    /// Hash and re-check every blob of the newest snapshot at startup. Off by
    /// default: it reads the whole store, which is a second or two per gigabyte and
    /// not something to do on every restart. "/datbackup verify" runs it on demand.
    /// </summary>
    public bool VerifyOnStartup { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The store path, with the "beside the mod" default resolved.</summary>
    public string ResolvedBackupDirectory =>
        string.IsNullOrWhiteSpace(BackupDirectory)
            ? Path.Combine(Mod.ModPath, "DatBackup")
            : BackupDirectory;

    /// <summary>The dat folder, with the "ask ACE" default resolved.</summary>
    public string ResolvedDatDirectory =>
        string.IsNullOrWhiteSpace(DatDirectory)
            ? ConfigManager.Config?.Server?.DatFilesDirectory ?? ""
            : DatDirectory;

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path}");
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
