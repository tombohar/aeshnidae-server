namespace Aeshnidae.AdminAudit;

/// <summary>What kind of privileged action a record describes.</summary>
public enum AuditKind
{
    /// <summary>A privileged command was accepted for execution, or refused.</summary>
    Command,

    /// <summary>ACE narrated an admin action on its own audit channel (delete, smite, teleport, server property...).</summary>
    Narrated,

    /// <summary>An object was conjured into someone's inventory while a privileged command was running.</summary>
    ItemCreated,

    /// <summary>A privileged character dropped an object on the ground.</summary>
    ItemDropped,

    /// <summary>A privileged character handed an object to someone.</summary>
    ItemGiven,

    /// <summary>XP or luminance granted by command (XpType.Admin).</summary>
    Grant,

    /// <summary>Aeshnidae.Bank deposit or withdrawal.</summary>
    Bank,

    /// <summary>Aeshnidae.XpCurrency transfer between players.</summary>
    XpTransfer,

    /// <summary>Aeshnidae.InstancesNoDat created a landblock copy.</summary>
    Instance,
}

/// <summary>
/// One line of the audit trail.
///
/// Serialised as a single JSON object per line (JSONL): appendable without rewriting,
/// greppable with plain tools, and still machine-readable. Property names are short
/// and lowercase because a busy server writes a lot of these and they are read far
/// more often with grep than with a parser.
/// </summary>
public sealed class AuditEvent
{
    [JsonPropertyName("ts")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>Character name, or "CONSOLE" for a server-console command.</summary>
    [JsonPropertyName("actor")]
    public string Actor { get; set; } = "";

    /// <summary>Account the actor was logged in under. The character can be renamed; the account is the real identity.</summary>
    [JsonPropertyName("account")]
    public string? Account { get; set; }

    [JsonPropertyName("access")]
    public string? Access { get; set; }

    /// <summary>"ingame" or "console".</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "ingame";

    /// <summary>Command name, or a short verb for non-command events.</summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = "";

    /// <summary>Human-readable summary - this is the line you actually read when scanning.</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    /// <summary>Who or what the action was aimed at, when there is one.</summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>"ok", "denied", or a CommandHandlerResponse name.</summary>
    [JsonPropertyName("outcome")]
    public string? Outcome { get; set; }

    /// <summary>Where the actor was standing, as a LOC string.</summary>
    [JsonPropertyName("loc")]
    public string? Location { get; set; }

    /// <summary>
    /// True for commands that only look at things. Kept rather than dropped, so the
    /// record of an admin probing around before acting survives, but the Discord feed
    /// can skip them.
    /// </summary>
    [JsonPropertyName("readonly")]
    public bool ReadOnly { get; set; }

    /// <summary>The command this event happened underneath, when it was caused by one.</summary>
    [JsonPropertyName("via")]
    public string? Via { get; set; }

    /// <summary>Kind-specific extras: amounts, guids, weenie ids.</summary>
    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; set; }

    public AuditEvent With(string key, object? value)
    {
        if (value is null)
            return this;

        (Data ??= new())[key] = value.ToString() ?? "";
        return this;
    }

    /// <summary>The one-line form used for the console, the Discord feed and /adminaudit tail.</summary>
    public string ToLine()
    {
        var time = DateTime.TryParse(Timestamp, null, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed.ToLocalTime().ToString("HH:mm:ss")
            : Timestamp;

        var who = string.IsNullOrEmpty(Account) ? Actor : $"{Actor} ({Account})";
        var outcome = Outcome is null or "ok" ? "" : $" [{Outcome}]";

        return $"{time} {who}: {Detail}{outcome}";
    }
}
