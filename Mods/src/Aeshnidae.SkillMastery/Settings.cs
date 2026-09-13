namespace Aeshnidae.SkillMastery;

/// <summary>
/// One tunable segment of the mastery cost curve.
///
/// A band covers skill points from the previous band's <see cref="UpToSkillPoints"/>
/// up to and including its own, so bands are written in ascending order and the
/// first one starts at 0.
/// </summary>
public class CostBand
{
    /// <summary>Highest skill point total this band applies to.</summary>
    public int UpToSkillPoints { get; set; }

    /// <summary>
    /// What one whole skill point costs in this band, as a multiple of the last
    /// retail rank for that skill's advancement class - 306,860,483 xp trained,
    /// 350,046,134 specialized. So 1.0 is "a skill point costs what retail's most
    /// expensive rank cost", and 0.5 is half that.
    ///
    /// Priced per skill point rather than per rank on purpose: RanksPerSkillPoint
    /// then only controls how finely the purchase is chopped up, never how much it
    /// costs in total. Change the divisor from 10 to 2 and the price is identical -
    /// you just buy it in five steps instead of fifty.
    /// </summary>
    public double CostPerSkillPoint { get; set; } = 1.0;

    /// <summary>
    /// Compounding within the band, applied per rank from the band's first rank.
    /// 1.0 is flat, which is the point of having bands - put the shape in the
    /// multipliers where you can see it rather than in an exponent.
    /// </summary>
    public double Growth { get; set; } = 1.0;
}

public class Settings
{
    public const string FileName = "Settings.json";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Refuse mastery until the skill is at its retail maximum.
    ///
    /// OFF, because the price already does this job. At CostPerSkillPoint 1.0 a
    /// mastery point costs what retail's most expensive rank cost, so early retail
    /// ranks - orders of magnitude cheaper - are always the better buy. Nobody takes
    /// mastery early by choice, and a hard gate only adds friction.
    ///
    /// The invariant that actually matters is the one to watch when editing bands:
    /// while a mastery point costs more than the retail rank it competes with,
    /// players raise retail first by choice. Price a band below that and they will
    /// skip retail entirely - mastery survives enlightenment and retail does not, so
    /// cheap mastery makes enlightenment toothless.
    /// </summary>
    public bool RequireRetailCap { get; set; } = false;

    /// <summary>
    /// Mastery ranks that make up one skill point. 1: one /raise is one whole point.
    ///
    /// 10 was tried twice (2026-09-12): each raise a tenth of a point at a tenth of the
    /// price. It never changed what a point cost, only chopped the purchase up, and
    /// buying fractions that do nothing until the tenth lands read as odd. Tom's call:
    /// keep raises whole and put the weight in <see cref="PointPriceMultiplier"/>
    /// instead. The readouts still cope with any value here.
    /// </summary>
    public int RanksPerSkillPoint { get; set; } = 1;

    /// <summary>
    /// How much dearer a mastery point is than the retail curve says.
    ///
    /// 10: the first mastery point costs ten times what rank 209 would have (3.31B
    /// trained, 3.78B specialized), and the curve climbs from there at the retail rate.
    /// This is where the "1/10th of a point" mechanic's weight went when the fractions
    /// were dropped - same power per Radiance as ten tenths at full price each would
    /// have been. Baked into the generated band's CostPerSkillPoint; edit the band to
    /// change a live server, this only shapes a fresh Settings.json.
    /// </summary>
    public double PointPriceMultiplier { get; set; } = 10.0;

    /// <summary>
    /// Ceiling on mastery skill points per skill.
    ///
    /// Mostly ceremonial under the retail curve - the price does the capping. At 1.0787
    /// per point, +50 costs about one full climb to level 275 in total, and +100 costs
    /// forty of them. Nobody reaches 200; the number is here so /mastery can state a
    /// ceiling that is a number rather than "when the sun burns out".
    /// </summary>
    public int MaxSkillPoints { get; set; } = 200;

    /// <summary>Width of each generated band, in skill points.</summary>
    public int BandSize { get; set; } = 10;

    /// <summary>
    /// The cost curve.
    ///
    /// THE DEFAULT IS THE RETAIL SKILL TABLE, CONTINUED. From rank 150 to 208 the retail
    /// trained-skill table is a clean geometric progression - each rank costs 1.0787x
    /// the one before, steady to four decimal places (specialized runs a little steeper
    /// and noisier, ~1.099; one multiplier is used for both, which under-prices
    /// specialized slightly and is the simpler thing to reason about). So the first
    /// mastery point is priced as rank 209 would have been, the second as rank 210, and
    /// so on, times <see cref="PointPriceMultiplier"/>: one band covering everything,
    /// CostPerSkillPoint 10.787 (ten times the step from 208 to 209), Growth 1.0787
    /// (each step after). Players already know this curve from raising every skill they
    /// have; mastery is that curve not stopping, with a 10x weight on it.
    ///
    /// What it costs, in Radiance (= experience), trained, at the 10x default
    /// (a climb from 1 to 275 is about 191B):
    ///     +1     3.3B       +10   6.5B     (48B total - a quarter of a climb)
    ///     +30   29.8B       (366B total - about two climbs)
    ///     +50  135.5B       (1.8T total - about nine and a half climbs)
    ///     +100  6.0T        (82T total - over four hundred climbs; nobody gets here)
    ///
    /// Generated on first run and then editable: split it into segments to reshape a
    /// stretch without touching the rest. Past the last band,
    /// <see cref="DefaultCostPerSkillPoint"/> applies flat.
    /// </summary>
    public List<CostBand> Bands { get; set; } = new();

    /// <summary>The retail table's per-rank multiplier at its tail. See <see cref="Bands"/>.</summary>
    public const double RetailGrowth = 1.0787;

    public double DefaultCostPerSkillPoint { get; set; } = 1.0;

    /// <summary>Writes the retail-curve band the first time the mod runs. See <see cref="Bands"/>.</summary>
    public bool EnsureBands()
    {
        if (Bands.Count > 0)
            return false;

        Bands.Add(new CostBand
        {
            UpToSkillPoints = MaxSkillPoints,
            CostPerSkillPoint = Math.Max(0.0001, PointPriceMultiplier) * RetailGrowth,
            Growth = RetailGrowth,
        });

        return true;
    }

    /// <summary>The band a given skill point total sits in.</summary>
    public CostBand BandFor(int skillPoints)
    {
        foreach (var band in Bands)
        {
            if (skillPoints < band.UpToSkillPoints)
                return band;
        }

        return new CostBand { UpToSkillPoints = int.MaxValue, CostPerSkillPoint = DefaultCostPerSkillPoint };
    }

    /// <summary>First skill point covered by a band, for the within-band growth exponent.</summary>
    public int BandStart(CostBand band)
    {
        var start = 0;

        foreach (var b in Bands)
        {
            if (ReferenceEquals(b, band))
                return start;

            start = b.UpToSkillPoints;
        }

        return start;
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
