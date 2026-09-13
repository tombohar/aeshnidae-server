namespace Aeshnidae.DiscordRelay;

/// <summary>
/// The tap into ACE's chat: the exact point where a global chat message has been
/// delivered.
///
/// <c>TurbineChatHandler.LogTurbineChat</c> is called once per message, on the last
/// line of <c>TurbineChatReceived</c>, and every rejection path returns before
/// reaching it - gags, <c>chat_echo_only</c>, the account-age / played-time / level
/// gates, and a channel switched off with <c>chat_disable_trade</c>. So a patch here
/// relays exactly the messages players actually saw, exactly once each.
///
/// That is why this is the hook rather than the more obvious
/// <c>GameMessageTurbineChat</c> constructor: that runs before the gates, so it would
/// also push refused messages to Discord, and it runs for the acknowledgement packets
/// too.
///
/// A prefix rather than a postfix, because LogTurbineChat itself returns early when
/// the matching <c>chat_log_*</c> server property is off. Whether the operator wants
/// chat in the server log is a separate question from whether they want it in Discord.
/// The prefix returns void, so ACE's own logging is untouched either way.
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>
    /// Private in ACE, so it is referenced by name. <see cref="Mod.Initialize"/> checks
    /// it still resolves before patching, rather than letting a rename upstream leave
    /// the mod stuck Inactive.
    /// </summary>
    internal const string LogTurbineChat = "LogTurbineChat";

    /// <summary>
    /// Parameter names must match ACE's exactly - that is how Harmony binds them.
    /// Runs on the network thread mid-packet, so it hands off to a queue and returns.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(TurbineChatHandler), LogTurbineChat)]
    public static void PreLogTurbineChat(uint channelID, string name, string message, uint senderID, ChatType chatType)
    {
        try
        {
            Mod.Relay?.SubmitChat(chatType, name, message);
        }
        catch (Exception ex)
        {
            // Throwing here would surface as an ACE bug on every line of global chat.
            ModManager.Log($"[{Mod.Name}] relay of {chatType} from {name} failed: {ex}", ModManager.LogLevel.Error);
        }
    }
}
