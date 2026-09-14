namespace Aeshnidae.Bank;

/// <summary>
/// Settings.json in the deployed mod folder. Edit and /bankreload.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>
    /// Which Legendary Key sizes a withdrawal may hand out, in charges. Payout is
    /// greedy largest-first, so this decides what you actually get.
    ///
    /// The full set the world database offers is 25, 10, 5, 4, 3, 2, 1:
    ///   [25,10,5,4,3,2,1]  fewest items - "withdraw 24" gives 10+10+4 (3 keys)
    ///   [25,1]             only the two common ones - "withdraw 3" gives 3 singles
    ///
    /// Deposits always accept every variant regardless of this list.
    /// </summary>
    public int[] LegendaryKeyPayout { get; set; } = { 25, 10, 5, 4, 3, 2, 1 };

    /// <summary>Sturdy Iron sizes: 50 is the Keyring, 1 the plain key.</summary>
    public int[] SturdyIronKeyPayout { get; set; } = { 50, 1 };

    /// <summary>Largest single deposit or withdrawal. 0 for no limit.</summary>
    public long MaxTransaction { get; set; } = 0;

    // ---- RADIANCE AND RESONANCE ---------------------------------------------
    //
    // Two earned currencies with no physical form. They are never carried, only
    // banked, which is what makes them account-wide without a rule saying so.

    /// <summary>Experience from kills and quests pays radiance.</summary>
    public bool RadianceEnabled { get; set; } = true;

    /// <summary>
    /// Radiance per point of experience RECEIVED - after the server rate, the quest
    /// modifier, enchantments and any fellowship split.
    ///
    /// 1.0, and it should stay 1.0: Radiance is experience's mirror, and the mastery
    /// prices in Aeshnidae.SkillMastery are the retail skill table continued, which only
    /// reads correctly if a Radiance is worth exactly an experience point. Change this
    /// and every mastery price silently changes with it.
    ///
    /// (Until 2026-09-12 this was RadiancePerXp = 0.001 against a creature's raw
    /// XpOverride, kills only. Balances earned under that rule were multiplied by 500
    /// when the rule changed, which is the ratio between the two.)
    /// </summary>
    public double RadiancePerExperience { get; set; } = 1.0;

    /// <summary>Quest completions pay resonance.</summary>
    public bool ResonanceEnabled { get; set; } = true;

    /// <summary>Resonance for completing a quest, when it has no override below.</summary>
    public long ResonancePerQuest { get; set; } = 10;

    /// <summary>
    /// Per-quest resonance, by quest name, for the ones worth more than the flat rate.
    /// Names are the registry names, as /myquests reports them.
    /// </summary>
    public Dictionary<string, long> ResonanceOverrides { get; set; } = new();

    /// <summary>
    /// Registry entries that are bookkeeping rather than quests, as wildcard patterns
    /// (* matches anything; case-insensitive): pickup and turn-in timers, the wait
    /// stamp a kill task sets at hand-in, stipend flags. They pay nothing, whatever the
    /// flat rate says. Kill counting itself is handled by the counting guard in
    /// Patches (HandleKillTask and Increment), whatever the counter is called - a
    /// task pays once, on the kill that completes it - so it needs no pattern here.
    /// </summary>
    public string[] ResonanceSkipPatterns { get; set; } = { "*Timer*", "*Wait_*", "*Stipend*", "*Cooldown*" };

    private System.Text.RegularExpressions.Regex[]? _resonanceSkip;

    /// <summary>Does a registry name match <see cref="ResonanceSkipPatterns"/>?</summary>
    public bool IsResonanceSkipped(string questName)
    {
        _resonanceSkip ??= (ResonanceSkipPatterns ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(p).Replace("\\*", ".*") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled))
            .ToArray();

        return !string.IsNullOrEmpty(questName) && _resonanceSkip.Any(r => r.IsMatch(questName));
    }

    /// <summary>
    /// Pay only the first time a quest is solved, rather than every completion.
    ///
    /// OFF, deliberately. A currency that ignores repeatable content makes repeatable
    /// content worthless, and dailies are where a steady economy should come from. Turn
    /// it on if a fast-repeating quest turns out to be a printing press.
    /// </summary>
    public bool ResonanceFirstSolveOnly { get; set; } = false;

    /// <summary>Tell players in chat as they earn radiance.</summary>
    public bool AnnounceRadiance { get; set; } = true;

    /// <summary>
    /// Seconds to gather radiance into one chat line.
    ///
    /// 0, the default: every kill reports its own line, so the number is always next to
    /// the monster that paid it. That is the point of a per-kill currency - you can see
    /// what a thing is worth without doing arithmetic afterwards.
    ///
    /// Set it to something like 15 if the chat volume becomes a problem on a fast
    /// farming loop, and a clearing will read as "You gain 340 Radiance." once instead
    /// of thirty times.
    ///
    /// Only radiance is affected either way. Resonance is always immediate.
    /// </summary>
    public double AnnounceRollupSeconds { get; set; } = 0;

    public bool AnnounceResonance { get; set; } = true;

    /// <summary>
    /// How often earned currency is written to the database, in seconds.
    ///
    /// Awards happen inside a landblock tick, and a MySQL round trip there would put
    /// database latency in the world loop, so they are buffered in memory and written
    /// by a background flush. Balances always read as balance + pending, so nothing
    /// looks missing in between.
    ///
    /// The cost is that a hard crash loses at most this many seconds of earnings. Set
    /// to 0 to write every award synchronously instead - correct, durable, and paid for
    /// on the world thread.
    /// </summary>
    public double FlushSeconds { get; set; } = 10;

    /// <summary>
    /// Characters may opt into having earned luminance go straight to the bank, with
    /// /bank autolum on.
    ///
    /// Off for a character until they ask for it, and worth understanding before you
    /// leave this enabled: banked luminance is not subject to MaximumLuminance, so a
    /// flagged character never loses an award to the cap. That removes a designed
    /// constraint - in retail the cap is what forces a choice between hoarding for an
    /// aura and spending now. Turn this off server-wide to keep that tension.
    /// </summary>
    public bool AllowLuminanceAutoBank { get; set; } = true;

    /// <summary>Tell a flagged character in chat each time luminance is banked for them.</summary>
    public bool AnnounceLuminanceBanked { get; set; } = true;

    /// <summary>Players can send radiance and resonance to each other with /bank pay.</summary>
    public bool AllowPlayerTransfers { get; set; } = true;

    /// <summary>
    /// Fraction of a transfer destroyed in transit, 0 to 1.
    ///
    /// Zero: what you send is what arrives. It was 0.02 until 2026-09-14, as the one sink
    /// the mod had; Tom took it out. Radiance already has a sink that matters - skill
    /// mastery destroys it - and a fee on handing it to a friend only taxed the social
    /// use of the currency. Set it above zero if the economy ever needs draining.
    /// </summary>
    public double TransferTax { get; set; } = 0.0;

    /// <summary>
    /// Smallest transfer allowed. It kept a tax from being rounded away one unit at a
    /// time; with no tax it is simply a floor against spamming /b pay. Radiance is on
    /// the experience scale - one kill is thousands - so this sits where it still means
    /// something.
    /// </summary>
    public long MinimumTransfer { get; set; } = 100_000;

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
