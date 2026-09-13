namespace Aeshnidae.MoveGuard;

public enum Mode
{
    /// <summary>Patched but inert - nothing is measured.</summary>
    Off,

    /// <summary>Measure and report. Nothing is rejected. Start here.</summary>
    Log,

    /// <summary>Reject and rubber-band. Geometry rejections need EnforceGeometry too.</summary>
    Enforce,
}

/// <summary>
/// Settings.json in the deployed mod folder. Edit and /moveguard reload.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>Off, Log or Enforce. Log is the default on purpose - see the Readme.</summary>
    public string Mode { get; set; } = "Log";

    // ------------------------------------------------------------------- speed

    /// <summary>
    /// How far over the player's real top speed a move may be before it counts.
    ///
    /// The ceiling is computed per player from their Run skill and the run animation,
    /// so this only has to absorb what that formula does not: jump-running (a running
    /// jump carries you faster than running), sliding down slopes, and rounding.
    /// 1.5 is generous for all of those and still a tenth of what a blink covers.
    /// </summary>
    public double SpeedTolerance { get; set; } = 1.5;

    /// <summary>
    /// Units added to every allowance regardless of elapsed time, so a tiny move
    /// after a tiny interval is never a rounding-error violation.
    /// </summary>
    public double SlackUnits { get; set; } = 3.0;

    /// <summary>
    /// The longest gap a single position update may account for, in seconds.
    ///
    /// Position updates arrive about once a second while moving, but lag bunches
    /// them: after a two-second stall the next update legitimately covers two seconds
    /// of running. This caps how much a gap is worth. A player standing still for a
    /// minute then blinking is allowed exactly this many seconds of running distance -
    /// so the number is the length of the longest blink that gets through, in seconds
    /// of running. Lower is stricter and rubber-bands real players during longer lag
    /// spikes; the default trades that against a ~60-unit blink from standstill.
    /// </summary>
    public double MaxCatchUpSeconds { get; set; } = 2.5;

    /// <summary>
    /// Seconds after a teleport (portal, recall, lifestone, admin) during which nothing
    /// is judged. The client is still sending positions from where it was, and ACE
    /// itself notes this as a known quirk.
    /// </summary>
    public double TeleportGraceSeconds { get; set; } = 3.0;

    /// <summary>
    /// Run-animation speed in units per second, used only if the player's motion
    /// table cannot be read. Human run animation is a little over 4.
    /// </summary>
    public double FallbackRunSpeed { get; set; } = 4.5;

    // ---------------------------------------------------------------- geometry

    /// <summary>
    /// Also ask the physics engine whether the requested position is reachable from
    /// the current one - the same collision transition ACE already runs and then
    /// ignores. This is what sees a short blink through a wall, which is too short
    /// for the speed check to notice.
    /// </summary>
    public bool CheckGeometry { get; set; } = true;

    /// <summary>
    /// How far, in units, the physics-resolved end position may fall short of the
    /// requested one before it counts. Physics slides you along a wall rather than
    /// failing, so a wall in the way shows up as a large shortfall; ordinary
    /// disagreement between client and server physics is well under a unit.
    /// </summary>
    public double GeometryTolerance { get; set; } = 2.0;

    /// <summary>
    /// Reject geometry violations too, not only speed. OFF by default because
    /// server physics does disagree with the client in a handful of honest cases -
    /// doors that closed during lag, jumps across cell boundaries, a monster the
    /// server thinks is in the way - and rubber-banding an honest player is worse
    /// than logging a cheat. Read the log for a while first.
    /// </summary>
    public bool EnforceGeometry { get; set; } = false;

    // ----------------------------------------------------------------- strikes

    /// <summary>Strikes older than this no longer count toward the thresholds below.</summary>
    public int StrikeWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Server-log lines per player per window. Strikes past this are still counted,
    /// audited and enforced - only the individual log line is dropped, so a false
    /// positive that hits everyone cannot flood the journal.
    /// </summary>
    public int LogPerWindow { get; set; } = 5;

    /// <summary>
    /// Post to the audit channel once a player has this many strikes in the window,
    /// and again for every further multiple. One rejected position is lag; three in a
    /// minute is a pattern worth a staff member's eyes.
    /// </summary>
    public int AuditAfterStrikes { get; set; } = 3;

    /// <summary>
    /// Disconnect a player at this many REJECTED positions in the window - logged-only
    /// strikes never count toward this. 0 disables. Enforce
    /// mode already makes cheating useless by holding the player where the server
    /// says they are; this is for when you would rather they were not online at all.
    /// </summary>
    public int KickAfterStrikes { get; set; } = 0;

    /// <summary>Tell the player when a position is rejected. Off: a silent snap back.</summary>
    public bool TellPlayer { get; set; } = false;

    public string PlayerMessage { get; set; } = "The server could not accept that movement.";

    /// <summary>
    /// Staff at or above this level are logged like anyone else but never
    /// rubber-banded or kicked. Admin, Developer, Envoy, Sentinel, Advocate, Player.
    /// </summary>
    public string ExemptAtOrAbove { get; set; } = "Admin";

    // ----------------------------------------------------------------- helpers

    public Mode ModeValue =>
        Enum.TryParse<Mode>(Mode, true, out var m) ? m : MoveGuard.Mode.Log;

    public AccessLevel ExemptLevel =>
        Enum.TryParse<AccessLevel>(ExemptAtOrAbove, true, out var lvl) ? lvl : AccessLevel.Admin;

    public bool IsExempt(Player? player) =>
        player?.Session is { } s && s.AccessLevel >= ExemptLevel;

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
