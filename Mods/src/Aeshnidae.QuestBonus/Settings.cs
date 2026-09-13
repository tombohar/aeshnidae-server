namespace Aeshnidae.QuestBonus;

/// <summary>
/// Read from Settings.json in the deployed mod folder
/// (C:\ACEPublic\Mods\Aeshnidae.QuestBonus\Settings.json), written with defaults
/// on first run.
///
/// To retune live: edit the file, then "/mod restart Aeshnidae.QuestBonus" or
/// "/qb reload". Both re-read this and resync every online character.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// XP multiplier gained per quest point: 0.001 = +0.1% per point.
    /// With <see cref="DefaultPoints"/> at 1, that is +0.1% per solved quest,
    /// so 50 quests give +5% and 200 give +20%.
    /// </summary>
    public double BonusConversion { get; set; } = 0.001;

    /// <summary>Points for a solved quest with no explicit entry in <see cref="QuestWeights"/>.</summary>
    public double DefaultPoints { get; set; } = 1.0;

    /// <summary>
    /// Ceiling on the final multiplier, so a very long-lived character cannot run
    /// away with it. 2.0 = at most double XP. Set to 0 to disable the cap.
    /// </summary>
    public double MaxMultiplier { get; set; } = 2.0;

    /// <summary>Master switch for the on-quest messages below.</summary>
    public bool NotifyQuest { get; set; } = true;

    /// <summary>
    /// Shown when a quest first starts counting toward the bonus.
    ///
    /// Placeholders:
    ///   {quest}  quest name, e.g. ChasingOswaldDone
    ///   {points} points gained or lost
    ///   {bonus}  new total bonus, e.g. +4.2%
    ///   {qp}     new total quest points
    ///
    /// Set to "" to stay silent on gains.
    /// </summary>
    public string QuestGainedMessage { get; set; } =
        "Way to go ya filthy animal.. You've gained QB by completing {quest}!";

    /// <summary>Shown when a quest stops counting (removed, erased or decremented to zero).</summary>
    public string QuestLostMessage { get; set; } =
        "You've lost {points} QB from {quest}. Now at {bonus} XP.";

    /// <summary>Tell the player how much every single XP award was boosted. Very chatty.</summary>
    public bool NotifyExp { get; set; } = false;

    /// <summary>
    /// Per-quest overrides, by quest name. Anything not listed is worth
    /// <see cref="DefaultPoints"/>. Set a quest to 0 to exclude it.
    ///
    /// Quests.txt in the source folder lists all 4,180 known quest names in this
    /// exact format, ready to paste. The defaults below exclude the recurring
    /// stipend timers, which are bookkeeping flags rather than real quests.
    /// </summary>
    public Dictionary<string, double> QuestWeights { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["StipendsCollectedInAMonth"] = 0,
        ["StipendTimer_08"] = 0,
        ["StipendTimer_Monthly"] = 0,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Without this the default HTML-safe encoder writes apostrophes as ',
        // which makes the message templates unpleasant to hand-edit.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
                loaded.Normalize();
                return loaded;
            }

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

    /// <summary>
    /// ACE matches quest names case-insensitively (QuestManager.GetQuest uses
    /// OrdinalIgnoreCase), but System.Text.Json deserializes into a plain
    /// case-sensitive dictionary. Rebuild it so "pathwardencomplete" in
    /// Settings.json still matches "PathwardenComplete" in the registry.
    /// </summary>
    private void Normalize() =>
        QuestWeights = new Dictionary<string, double>(
            QuestWeights ?? new Dictionary<string, double>(),
            StringComparer.OrdinalIgnoreCase);

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
