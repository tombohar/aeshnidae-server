namespace Aeshnidae.DiscordRelay;

/// <summary>
/// One Discord destination: which webhook to POST to, and how this mod should
/// identify itself when it does.
/// </summary>
public class ChannelSettings
{
    /// <summary>Relay this in-game channel at all. Turning it off leaves the URL in place.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Full https://discord.com/api/webhooks/&lt;id&gt;/&lt;token&gt; URL. Empty means
    /// "not wired up yet" and the channel is skipped silently.
    ///
    /// Treat this as a credential - it is unauthenticated write access to that Discord
    /// channel for anyone holding it. It only ever lives in Settings.json in the
    /// deployed mod folder, which Mods\.gitignore excludes, so it stays out of git.
    /// Rotate it in Discord (Edit Channel - Integrations) if it leaks.
    /// </summary>
    public string WebhookUrl { get; set; } = "";

    /// <summary>
    /// Overrides the webhook's own display name on every post. Empty leaves whatever
    /// the webhook is called in Discord, which is usually what you want.
    /// Discord rejects names containing "discord" and anything over 80 characters.
    /// </summary>
    public string Username { get; set; } = "";
}

/// <summary>
/// Per-mod configuration, read from Settings.json in the deployed mod folder
/// (next to the dll, not in source). Written with defaults on first run so it is
/// easy to find, and openable in-game with "/mod settings Aeshnidae.DiscordRelay".
/// </summary>
public class Settings
{
    public const string FileName = "Settings.json";

    /// <summary>Master switch. Off means nothing is queued and nothing is posted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Keyed by ACE's <see cref="ChatType"/> name. Anything not listed here is not
    /// relayed, so adding "LFG" or "Roleplay" with a webhook is all it takes to widen
    /// the bridge - no code change.
    /// </summary>
    public Dictionary<string, ChannelSettings> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["General"] = new(),
        ["Trade"]   = new(),
    };

    /// <summary>
    /// How long queued lines are allowed to accumulate before a post goes out.
    /// This is the rate limiter: Discord allows roughly 5 requests per 2 seconds per
    /// webhook, and batching a busy Trade channel into one post every couple of
    /// seconds stays comfortably clear of that no matter how loud the server gets.
    /// </summary>
    public double BatchSeconds { get; set; } = 2.0;

    /// <summary>
    /// Backstop for Discord being unreachable. Once a channel's queue is this deep,
    /// new lines are dropped rather than buffered forever - a chat bridge is not worth
    /// an unbounded allocation on a live server.
    /// </summary>
    public int MaxQueuedPerChannel { get; set; } = 200;

    /// <summary>Longest relayed message body, in characters. Longer ones are truncated.</summary>
    public int MaxMessageLength { get; set; } = 400;

    /// <summary>
    /// Line template. "{name}" is the speaking character, "{message}" the (escaped,
    /// truncated) text, "{channel}" the ChatType name.
    /// </summary>
    public string LineFormat { get; set; } = "**{name}**: {message}";

    /// <summary>Character names never relayed. Case-insensitive; useful for staff bots.</summary>
    public string[] IgnoredPlayers { get; set; } = Array.Empty<string>();

    /// <summary>Log every relayed line to the server log as well. Noisy; off by default.</summary>
    public bool LogRelayed { get; set; } = false;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// The destination for a chat type, or null if this channel is not bridged.
    /// Society sub-channels collapse onto "Society" so one entry covers all three.
    /// </summary>
    public ChannelSettings? For(ChatType chatType)
    {
        var key = Key(chatType);

        if (!Channels.TryGetValue(key, out var channel) || channel is null)
            return null;

        return channel.Enabled && !string.IsNullOrWhiteSpace(channel.WebhookUrl) ? channel : null;
    }

    /// <summary>Settings key for a chat type: the enum name, with the three society variants merged.</summary>
    public static string Key(ChatType chatType) => chatType switch
    {
        ChatType.SocietyCelHan or ChatType.SocietyEldWeb or ChatType.SocietyRadBlo => nameof(ChatType.Society),
        _ => chatType.ToString(),
    };

    public bool IsIgnored(string? name) =>
        !string.IsNullOrEmpty(name) && IgnoredPlayers.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));

    public static Settings Load(string modPath)
    {
        var path = Path.Combine(modPath, FileName);

        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();

                // A dictionary deserialised from JSON gets the default comparer, so
                // "general" would miss the "General" entry. Rebuild it case-insensitive.
                loaded.Channels = new Dictionary<string, ChannelSettings>(loaded.Channels ?? new(), StringComparer.OrdinalIgnoreCase);
                loaded.IgnoredPlayers ??= Array.Empty<string>();

                return loaded;
            }

            var defaults = new Settings();
            defaults.Save(modPath);
            ModManager.Log($"[{Mod.Name}] wrote default settings to {path} - add your webhook URLs there, then /discordrelay-reload");
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
