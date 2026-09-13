namespace Aeshnidae.DevKit;

/// <summary>
/// Per-mod configuration, read from Settings.json in the deployed mod folder.
///
/// The same dll runs on the live server and on staging; these settings are what make
/// the two behave differently. On live, everything that writes is off. On staging,
/// AllowImport is on and BaseSchema points at the pristine copy of live that
/// refresh-staging.sh leaves behind, which is what /wdiff and /submit compare against.
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    // ------------------------------------------------------------------------
    // Knobs. Two of these are the whole difference between live and staging.
    // ------------------------------------------------------------------------

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether /wimport may write to this server's world database. FALSE on live, on
    /// purpose: live changes go through the change ledger (push-content.sh) so they
    /// are snapshotted and recorded. TRUE on staging, where the whole point is to try
    /// things. Nothing else in this mod writes to the world database.
    /// </summary>
    public bool AllowImport { get; set; } = false;

    /// <summary>
    /// Schema holding the untouched copy of the live world database that this server's
    /// world was cloned from ("aeshnidae_staging_base" on staging). /wdiff, /lbdiff and
    /// /submit compare the world against it, so "what did I change" is answered exactly.
    /// Empty on live - there is nothing to compare against, and those commands say so.
    /// </summary>
    public string BaseSchema { get; set; } = "";

    /// <summary>
    /// Where /submit writes its change file and patch note before posting them to
    /// Discord, so a failed upload loses nothing. Empty means
    /// &lt;content_folder&gt;/submitted (content_folder is ACE's own property; see
    /// /modifystring content_folder).
    /// </summary>
    public string SubmitDirectory { get; set; } = "";

    /// <summary>
    /// Short label printed in command output and Discord posts so nobody mistakes a
    /// staging result for a live one. Empty falls back to the server's WorldName.
    /// </summary>
    public string ServerLabel { get; set; } = "";

    /// <summary>
    /// Discord webhook for the channel exports and submissions are posted to (#content,
    /// the developer channel). A credential: lives only in the deployed Settings.json,
    /// which is gitignored, and every status command prints it masked.
    /// </summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>Name shown on the webhook post. Empty keeps the webhook's own.</summary>
    public string Username { get; set; } = "Aeshnidae DevKit";

    /// <summary>
    /// Discord rejects attachments over its limit with a 413. 8 MB is the safe floor
    /// for an unboosted server; every weenie ever exported here has been under 200 KB.
    /// </summary>
    public int MaxAttachmentBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>/wfind stops listing after this many matches; narrow the search instead.</summary>
    public int MaxFindResults { get; set; } = 40;

    /// <summary>
    /// /wdiff and /lbdiff print at most this many changed lines in chat. The full diff
    /// always goes into the submitted change file, so the cap only limits chat spam.
    /// </summary>
    public int MaxDiffLines { get; set; } = 80;

    // ------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public bool HasWebhook =>
        !string.IsNullOrWhiteSpace(WebhookUrl) && WebhookUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public bool HasBase => !string.IsNullOrWhiteSpace(BaseSchema);

    public string Label => string.IsNullOrWhiteSpace(ServerLabel)
        ? (ConfigManager.Config?.Server?.WorldName ?? "server")
        : ServerLabel;

    /// <summary>The webhook with its token hidden, for status output.</summary>
    public string MaskedWebhook
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WebhookUrl)) return "(not set)";
            var i = WebhookUrl.LastIndexOf('/');
            return i > 0 && WebhookUrl.Length - i > 8 ? WebhookUrl[..(i + 5)] + "…" : "(set)";
        }
    }

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path} - set WebhookUrl to enable exports; AllowImport and BaseSchema on staging only");
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
