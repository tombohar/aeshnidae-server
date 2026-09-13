namespace Aeshnidae.PortalAccess;

/// <summary>
/// Two switches, because the two halves are different decisions.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// Drop the minimum level on every portal, so any character can enter any
    /// dungeon. 933 portal weenies in the current world database set one, from
    /// level 1 to 275.
    ///
    /// This is also what makes the old enlightenment exploit pointless rather than
    /// merely blocked. Players used to enlighten in portal space so the lifestone
    /// teleport could not land, keeping a level 1 character inside high-tier
    /// content and re-levelling off mobs far above them. With no minimum to dodge,
    /// there is nothing to gain by dodging it. Aeshnidae.Enlightenment closes the
    /// mechanism as well - see its RequireProximityToNpc.
    /// </summary>
    public bool RemoveMinLevel { get; set; } = true;

    /// <summary>
    /// Drop the maximum level too, opening low-level-only content to maxed
    /// characters. 127 portal weenies set one, from level 5 to 150.
    ///
    /// Separate from the minimum on purpose, and the one to think twice about: it
    /// is the half that lets a 275 walk into newbie content, which is a content
    /// decision rather than an access one. Nothing about the enlightenment exploit
    /// needs it - that was entirely about minimums. Set false to keep the caps.
    ///
    /// ACE has its own switch for this (the server property
    /// use_portal_max_level_requirement); this setting overrides it for portals
    /// either way, so set both consistently or leave the property alone.
    /// </summary>
    public bool RemoveMaxLevel { get; set; } = true;

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
