namespace Aeshnidae.MaxLevel;

/// <summary>
/// Settings.json in the deployed mod folder. Changing MaxLevel needs a server
/// restart, because the XP table is only rebuilt when the DATs load.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// New character level cap. Retail is 275; anything at or below that leaves
    /// the table untouched.
    /// </summary>
    public int MaxLevel { get; set; } = 500;

    /// <summary>Grant a skill credit every N levels beyond retail's 275.</summary>
    public int SkillCreditInterval { get; set; } = 25;

    /// <summary>How many credits each of those milestones grants.</summary>
    public uint SkillCreditsPerInterval { get; set; } = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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
