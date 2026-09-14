namespace Aeshnidae.InstancesNoDat;

public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// Off by default, and it should stay off until the routing has been proven with
    /// two players in two copies of one dungeon. The patches all decline to act when
    /// no copy is open, so a loaded-but-unused mod is stock behaviour - but there is
    /// no reason to have it loaded before it has been tested.
    /// </summary>
    public bool Enabled { get; set; } = false;

    // ---- THE TWO NUMBERS ----------------------------------------------------
    //
    // A copy is scaled by the enlightenment of whoever opened it, linearly: at 5.0
    // percent each, ten enlightenments is +50%, not 1.05^10. Linear because these are
    // meant to be readable from the settings file - if you want to know what EL 10
    // feels like, it is ten times the number below, and nothing else.
    //
    // Difficulty moves monster skills, health and damage. Reward moves the experience
    // and luminance their corpses pay out. Set them to the same value and a copy is as
    // much richer as it is harder, which is the point.

    /// <summary>Master switch. Off means copies are exactly stock difficulty.</summary>
    public bool ScaleWithEnlightenment { get; set; } = true;

    /// <summary>Percent harder, per enlightenment of the copy's owner.</summary>
    public double DifficultyPercentPerEnlightenment { get; set; } = 5.0;

    /// <summary>Percent richer, per enlightenment of the copy's owner.</summary>
    public double RewardPercentPerEnlightenment { get; set; } = 5.0;

    /// <summary>
    /// Stop counting enlightenments past this many. 0 is uncapped.
    ///
    /// Worth setting to something while the curve is young. Skills and health scale
    /// linearly and forever, and the first time somebody walks in at EL 40 you would
    /// rather find out you had a ceiling than that you did not.
    /// </summary>
    public int MaxEnlightenmentsCounted { get; set; } = 0;

    // Which levers each percent actually moves. All on is the intended shape; these
    // exist so a lever that turns out to be wrong can be switched off without
    // abandoning the other four.

    /// <summary>Monster attack and defence skills. The lever players feel as "it hits and dodges more".</summary>
    public bool ScaleCreatureSkills { get; set; } = true;

    /// <summary>Monster maximum health. The lever that makes fights longer.</summary>
    public bool ScaleCreatureHealth { get; set; } = true;

    /// <summary>
    /// Monster damage, via each creature's DamageRating.
    ///
    /// Be aware this is the lever your players resist hardest: incoming damage runs
    /// through 100 / (100 + their damage reduction rating), and an enlightened player
    /// has more of that from the luminance auras they kept. Expect it to land softer
    /// than the number suggests.
    /// </summary>
    public bool ScaleCreatureDamage { get; set; } = true;

    /// <summary>Experience from kills in a copy (the creature's XpOverride).</summary>
    public bool ScaleExperience { get; set; } = true;

    /// <summary>
    /// Luminance from kills in a copy (the creature's LuminanceAward).
    ///
    /// The one that matters most on a server with a raised level cap, because
    /// experience stops accruing entirely at max level - Player_Xp gates all of it
    /// behind `if (Level != maxLevel)` - while luminance does not.
    /// </summary>
    public bool ScaleLuminance { get; set; } = true;

    // -------------------------------------------------------------------------

    /// <summary>
    /// Highest copy number that may be created per landblock. Each copy is a full
    /// Landblock with its own physics cells and its own spawns, so this is the memory
    /// dial as much as the gameplay one.
    /// </summary>
    public int MaxCopiesPerLandblock { get; set; } = 8;

    /// <summary>
    /// Restrict copies to landblocks that have a dungeon interior.
    ///
    /// ON, and this is a safety rail rather than a preference. Interiors are
    /// self-contained: no adjacency, no terrain seams, no neighbouring landblocks to
    /// keep consistent. Copying a surface landblock would need all of that reasoned
    /// through, and none of it has been.
    /// </summary>
    public bool InteriorsOnly { get; set; } = true;

    /// <summary>
    /// Dungeons that hand out copies, and who shares each one.
    ///
    /// This is the whole mechanism, and it needs no portal weenies and no world database
    /// changes: name a dungeon here and EVERY route into it - a portal, a recall, a
    /// summon, an admin teleport, a login - routes the arriving player to the copy their
    /// group owns. Nothing else has to know instances exist, because entry is entry.
    /// </summary>
    public List<InstancedLandblock> Instanced { get; set; } = new();

    /// <summary>
    /// Close a copy once nobody is left in it. Off means copies persist until closed by
    /// hand, which is what you want while testing and not what you want in play.
    ///
    /// Note this is emptiness, not departure: an allegiance copy stays open while any
    /// member is still inside, which is the behaviour a shared hall needs.
    /// </summary>
    public bool CloseWhenEmpty { get; set; } = true;

    /// <summary>
    /// How long a copy stays open after the last player leaves. Long enough to survive
    /// a death and release, or a slow teleport, so nobody returns to a reset dungeon.
    ///
    /// Worth raising a long way for allegiance scope - a hall that evaporates two
    /// minutes after the last member logs off is not a hall.
    /// </summary>
    public double EmptyGraceSeconds { get; set; } = 120;

    /// <summary>
    /// Dying inside a copy costs no items and no coins.
    ///
    /// ON, and this is a safety rail rather than a generosity. A copy is destroyed once
    /// it empties, and its contents are destroyed with it rather than saved - which is
    /// correct, because saving them would write instance leftovers against the real
    /// landblock's id. But a death corpse IS contents: die, release to your lifestone,
    /// and the copy empties and takes your gear with it. Unrecoverable, and through no
    /// mistake of the player's.
    ///
    /// Applied to every scope, not only personal. The hazard does not depend on who
    /// shares the copy - an allegiance hall is destroyed the same way, just later.
    /// </summary>
    /// <summary>
    /// Logs every routing decision: who or what was sent to which copy, and why.
    ///
    /// On by default, because the failures this mod can produce are all invisible ones -
    /// a player quietly left on the shared landblock, an object spawned into the wrong
    /// copy, a reload that touched nothing. Each looks like "the instance is broken"
    /// and none of them logs anything on its own. Noisy is the correct trade while this
    /// is young; turn it off once it is boring.
    /// </summary>
    public bool LogRouting { get; set; } = true;

    public bool NoItemLossInCopies { get; set; } = true;

    /// <summary>landblock -> its configuration, for the lookups the hot paths do.</summary>
    public Dictionary<ushort, InstancedLandblock> InstancedMap()
    {
        var map = new Dictionary<ushort, InstancedLandblock>();

        foreach (var entry in Instanced ?? new List<InstancedLandblock>())
        {
            if (entry.TryParse(out var lb))
                map[lb] = entry;
            else
                ModManager.Log($"[{Mod.Name}] '{entry.Landblock}' is not a hex landblock id",
                               ModManager.LogLevel.Warn);
        }

        return map;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Scope reads as "Personal" / "Allegiance" rather than 0 / 2. This file is
        // edited by hand far more often than it is written by code.
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
