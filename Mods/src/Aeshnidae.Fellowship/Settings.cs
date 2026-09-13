namespace Aeshnidae.Fellowship;

public class Settings
{
    public const string FileName = "Settings.json";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many people may be in one fellowship. Retail is 9.
    ///
    /// ACE keeps this in a public static field, so it is simply assigned rather than
    /// patched - but raising it without extending <see cref="SharePercentages"/> is a
    /// bug, not a feature: ACE's own share table stops at 9 and falls through to 1.0,
    /// which would hand every member of a larger fellowship a full share each. The two
    /// settings are coupled, and this mod keeps them consistent.
    /// </summary>
    public int MaxFellows { get; set; } = 9;

    /// <summary>
    /// What fraction of earned experience each member receives, by fellowship size.
    /// The key is the number of members; 1 means soloing in a fellowship of one.
    ///
    /// Retail's curve is a single steep step from 1 to 2 members and then a steady
    /// -0.05 per member: 1.0, .75, .6, .55, .5, .45, .4, .35, .3. Generated to
    /// MaxFellows on first run, continuing that decline past 9, and then edited by hand.
    ///
    /// Note this is share PER MEMBER, not a split of a fixed pool - a fellowship of
    /// nine at .3 each yields 2.7x the experience one player would have earned. That is
    /// retail behaviour, and it is why the number falls as the group grows rather than
    /// staying at 1.0.
    /// </summary>
    public Dictionary<int, double> SharePercentages { get; set; } = new();

    /// <summary>
    /// Share for a fellowship larger than the table covers. A floor rather than a
    /// fallthrough, because ACE's own fallthrough is 1.0 and that is the bug this
    /// setting exists to prevent.
    /// </summary>
    public double ShareBeyondTable { get; set; } = 0.10;

    /// <summary>Fills in the retail curve, extended to MaxFellows, on first run.</summary>
    public bool EnsureSharePercentages()
    {
        if (SharePercentages.Count > 0)
            return false;

        // Retail, verbatim, for the range retail covers.
        var retail = new Dictionary<int, double>
        {
            [1] = 1.00, [2] = 0.75, [3] = 0.60, [4] = 0.55, [5] = 0.50,
            [6] = 0.45, [7] = 0.40, [8] = 0.35, [9] = 0.30,
        };

        for (var count = 1; count <= Math.Max(MaxFellows, 9); count++)
        {
            if (retail.TryGetValue(count, out var pct))
                SharePercentages[count] = pct;
            else
                // Continue retail's -0.05 per member, floored so it never reaches zero.
                SharePercentages[count] = Math.Max(ShareBeyondTable, 0.30 - 0.05 * (count - 9));
        }

        return true;
    }

    /// <summary>Share for a given member count, from the table or the floor.</summary>
    public double ShareFor(int members)
    {
        if (members <= 0)
            return 1.0;

        return SharePercentages.TryGetValue(members, out var pct) ? pct : ShareBeyondTable;
    }

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
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not read {FileName}, using defaults: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }

        return new Settings();
    }

    public void Save(string modPath)
    {
        try
        {
            File.WriteAllText(Path.Combine(modPath, FileName), JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not write {FileName}: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }
}
