namespace Aeshnidae.AdminAudit;

/// <summary>The live Discord feed. Optional - the JSONL file is the system of record.</summary>
public class DiscordSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Webhook for a *private* audit channel. Treat as a credential; it lives only in
    /// the deployed mod folder, which Mods\.gitignore excludes.
    /// </summary>
    public string WebhookUrl { get; set; } = "";

    public string Username { get; set; } = "Aeshnidae audit";

    /// <summary>
    /// Send events whose detail starts with a prefix to a different webhook. The
    /// audit channel gets busy, and FairPlay's notices - which arrive here via
    /// PlayerManager.BroadcastToAuditChannel - are a different audience from staff
    /// actions. Anything unmatched goes to <see cref="WebhookUrl"/>.
    ///
    ///   "Routes": { "[FairPlay]": "https://discord.com/api/webhooks/..." }
    /// </summary>
    public Dictionary<string, string> Routes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Post the look-but-don't-touch commands too. Off by default; they are still written to file.</summary>
    public bool IncludeReadOnly { get; set; } = false;

    /// <summary>Seconds of events coalesced into one post. Same rate-limit reasoning as the chat relay.</summary>
    public double BatchSeconds { get; set; } = 3.0;
}

public class LogSettings
{
    /// <summary>Empty means &lt;mod folder&gt;\audit.</summary>
    public string Directory { get; set; } = "";

    /// <summary>Days of audit files to keep. 0 keeps everything - which is a defensible choice for an audit trail.</summary>
    public int RetentionDays { get; set; } = 0;

    /// <summary>
    /// How long a record may sit in memory before it is on disk. Short by design: a
    /// crash must not take the trail with it.
    /// </summary>
    public double FlushSeconds { get; set; } = 1.0;
}

public class Settings
{
    public const string FileName = "Settings.json";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Lowest access level worth recording. "Advocate" means everything above a plain
    /// player. Set to "Player" to audit absolutely everyone, which is mostly useful
    /// when chasing a specific incident.
    /// </summary>
    public string MinimumAccessLevel { get; set; } = nameof(AccessLevel.Advocate);

    public LogSettings Log { get; set; } = new();

    public DiscordSettings Discord { get; set; } = new();

    /// <summary>
    /// Commands that only read state. Recorded, but marked <c>"readonly": true</c> so
    /// the Discord feed can skip them and a grep can filter them out. Extend freely -
    /// getting this list wrong costs noise, never coverage.
    /// </summary>
    public string[] ReadOnlyCommands { get; set; } =
    {
        "who", "listmods", "mod", "adminaudit", "serverstatus", "serverperformance",
        "gamecast", "telemetry", "whereami", "loc", "getpos", "listnearby",
        "showprops", "propertydump", "queryplayer", "finger", "lb", "landblockinfo",
        "commandhelp", "help", "acecommands", "version", "modlist", "discordrelay",
    };

    /// <summary>
    /// Journal commands that need no more than Player access, when a privileged account
    /// runs them.
    ///
    /// Off by default, because those are not uses of privilege - they are things any
    /// player could do, and an admin who plays the game normally, or runs a client
    /// plugin that polls something on a timer, buries the trail in them.
    ///
    /// Nothing substantive is lost: the actions that matter are captured by their own
    /// hooks regardless of this setting. A bank move is recorded by the BankService
    /// hook, an XP transfer by the Transfer hook, a conjured item by the inventory hook.
    /// What is dropped is only the echo of the command itself.
    ///
    /// Refused commands are always journalled, whatever this is set to - a player
    /// reaching for a command they do not have is the point of the whole exercise.
    /// </summary>
    public bool JournalPlayerLevelCommands { get; set; } = false;

    /// <summary>
    /// Player-access commands to journal anyway, when <see cref="JournalPlayerLevelCommands"/>
    /// is off.
    ///
    /// Empty by default, deliberately. The obvious entries would be the value-moving
    /// commands - bank and xp - but naming commands here is the wrong handle for them:
    /// "bank" and its alias "b" are the same action, so listing one and not the other
    /// makes the trail depend on which alias someone typed. Their effects are recorded
    /// by the BankService and Transfer hooks anyway, with the amount, the target and
    /// whether it succeeded - strictly better evidence than an echo of the command line.
    /// </summary>
    public string[] AlwaysJournalCommands { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Also record actions by these characters when they are below
    /// <see cref="MinimumAccessLevel"/>. For watching a specific account.
    /// </summary>
    public string[] AlwaysAudit { get; set; } = Array.Empty<string>();

    /// <summary>Try to hook Aeshnidae.Bank, Aeshnidae.XpCurrency and Aeshnidae.InstancesNoDat.</summary>
    public bool AuditSiblingMods { get; set; } = true;

    /// <summary>Also echo every record to the server log. Off by default - the JSONL file is better in every way.</summary>
    public bool EchoToServerLog { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary><see cref="MinimumAccessLevel"/> parsed, falling back to Advocate.</summary>
    public AccessLevel Threshold =>
        Enum.TryParse<AccessLevel>(MinimumAccessLevel, true, out var level) ? level : AccessLevel.Advocate;

    public bool IsReadOnlyCommand(string? command) =>
        !string.IsNullOrEmpty(command) &&
        ReadOnlyCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Should this command appear in the journal? <paramref name="required"/> is the
    /// access the command itself demands, not the actor's.
    /// </summary>
    public bool ShouldJournalCommand(string? command, AccessLevel required, bool denied)
    {
        if (denied || required > AccessLevel.Player || JournalPlayerLevelCommands)
            return true;

        return !string.IsNullOrEmpty(command)
               && AlwaysJournalCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsAlwaysAudited(string? name) =>
        !string.IsNullOrEmpty(name) &&
        AlwaysAudit.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Is this actor worth recording?</summary>
    public bool ShouldAudit(AccessLevel access, string? name) =>
        access >= Threshold || IsAlwaysAudited(name);

    public string ResolveDirectory(string modPath) =>
        string.IsNullOrWhiteSpace(Log?.Directory) ? Path.Combine(modPath, "audit") : Log!.Directory;

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

                loaded.Log ??= new LogSettings();
                loaded.Discord ??= new DiscordSettings();
                loaded.ReadOnlyCommands ??= Array.Empty<string>();
                loaded.AlwaysAudit ??= Array.Empty<string>();
                loaded.AlwaysJournalCommands ??= Array.Empty<string>();

                return loaded;
            }

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path}");
            return defaults;
        }
        catch (Exception ex)
        {
            // Defaults audit MORE than a broken config would. Failing open is the right
            // direction for an audit trail.
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
