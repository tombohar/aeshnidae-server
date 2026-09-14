namespace Aeshnidae.Bank;

/// <summary>
/// The HUD feed contract, shared between Aeshnidae.Hud and every mod that provides a panel.
///
/// A mod cannot reference another mod's types (each loads in its own context, so a
/// type shared across the boundary is not the same type), but every mod can read
/// ACE's own. So the one piece of state the feed needs - "this session asked for
/// panels" - lives on the Player as an ephemeral PropertyBool, and everything else
/// is a constant. Providers keep a verbatim copy of this file as HudFeed.cs in
/// their own namespace; keep them identical.
///
/// The wire contract: one chat message per panel on chat type 0x21, carrying JSON.
///
///     { "v":1, "id":"bank", "title":"...", "sub":"...",
///       "cols":[{"n":"Currency","w":140}, ...],
///       "rows":[{"k":"p","c":["Pyreals","1,234,567","20,000"],"col":"#9BE39B"}, ...],
///       "flds":[{"k":"amount","l":"Amount","w":90}, ...],
///       "acts":[{"l":"Deposit","c":"/b d {key} {amount}","row":true}, ...] }
///
/// Cells are strings, already formatted; the client lays them out and does nothing
/// clever with them. A field is a text box the player types into. An action is a
/// button; its command is sent to the server as if typed, with {key} replaced by the
/// selected row's key when the action needs a row and {<field>} by what was typed.
/// So a panel is a table with text boxes and buttons, which is enough for skills and
/// the bank alike, and every one of them ships with no client change. The client side
/// is Mods\Content\tools\AeshHud.
///
/// Why chat type 0x21: it is above every type the stock client's windows know, and
/// OpenAC's window filters hide it by default, so the line reaches the plugin without
/// being painted into the transcript. Nothing is sent to a session that has not asked
/// with /hud on, so a stock client never sees a line of it.
///
/// Why an ephemeral property: SetProperty on a PropertyBool listed in
/// EphemeralProperties.PropertiesBool goes to an in-memory dictionary on the object,
/// never to the biota and never to the shard database, and dies with the Player. That
/// is exactly the lifetime the flag wants - a stock client logging in tomorrow must
/// not inherit it - and the id is well clear of anything ACE (up to 9010) uses.
/// </summary>
public static class HudFeed
{
    public const int ChatType = 0x21;

    /// <summary>Set on the Player while its session wants panels. Ephemeral - see above.</summary>
    public const PropertyBool Listening = (PropertyBool)9501;

    /// <summary>
    /// Commands the HUD mod invokes with ["sync"] when a session turns the feed on or
    /// asks for a refresh: every registered command whose name starts with this.
    /// A provider registers "hud-&lt;panel&gt;" (Player, RequiresWorld) and sends its
    /// panel from it if the session is listening.
    /// </summary>
    public const string ProviderPrefix = "hud-";

    // Fully qualified on purpose: this file is copied into mods with different usings.
    private static readonly System.Text.Json.JsonSerializerOptions Json = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Make the flag ephemeral. Must run before the first SetProperty of it, or that
    /// write lands in the biota and is saved. Idempotent; every mod that touches the
    /// flag calls this from Initialize so the order mods load in does not matter.
    /// </summary>
    public static void RegisterProperty() => EphemeralProperties.PropertiesBool.Add(Listening);

    public static bool IsOn(Player? player) => player?.GetProperty(Listening) == true;

    public static void Send(Session session, object panel)
    {
        var text = System.Text.Json.JsonSerializer.Serialize(panel, Json);
        session.Network.EnqueueSend(new GameMessageSystemChat(text, (ChatMessageType)ChatType));
    }
}
