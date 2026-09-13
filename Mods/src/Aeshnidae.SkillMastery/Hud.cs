using System.Runtime.CompilerServices;

namespace Aeshnidae.SkillMastery;

/// <summary>
/// The HUD feed: panels the server describes and a client that can draw them draws.
///
/// The stock client cannot show a window it was not compiled with, so this exists for
/// clients that can - today the OpenAC plugin in Mods\Content\tools\AeshHud. The
/// contract is one chat message per panel, on chat type 0x21, carrying a JSON object:
///
///     { "v":1, "id":"skills", "title":"...", "sub":"...",
///       "cols":[{"n":"Skill","w":140}, ...],
///       "rows":[{"k":"meleedefense","c":["Melee Defense","Spec","462","520","3","1,234,567"],"col":"#9BE39B"}, ...],
///       "acts":[{"l":"Raise +1","c":"/raise {key} 1","row":true},{"l":"Refresh","c":"/hud sync"}] }
///
/// Cells are strings, already formatted; the client lays them out and does nothing
/// clever with them. An action is a button; its command is sent to the server as if
/// typed, with {key} replaced by the selected row's key when the action needs a row.
/// So a panel is a table with buttons, which is enough for skills, bank, mastery and
/// quest bonus alike, and every one of them ships with no client change.
///
/// Why chat type 0x21: it is above every type the stock client's windows know, and
/// OpenAC's window filters hide it by default, so the line reaches the plugin without
/// being painted into the transcript. Nothing is sent to a session that has not asked
/// with /hud on, so a stock client never sees a line of it.
/// </summary>
public static class Hud
{
    public const int ChatType = 0x21;

    /// <summary>Sessions that asked for the feed. Dies with the connection.</summary>
    private static readonly ConditionalWeakTable<Session, object> _on = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsOn(Session session) => _on.TryGetValue(session, out _);

    public static void TurnOn(Session session) => _on.AddOrUpdate(session, new object());

    public static void TurnOff(Session session) => _on.Remove(session);

    /// <summary>Re-send every panel this mod owns, if the session is listening.</summary>
    public static void Refresh(Player? player)
    {
        try
        {
            if (player?.Session is null || !IsOn(player.Session))
                return;

            Send(player.Session, SkillsPanel(player));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] hud refresh failed: {ex}", ModManager.LogLevel.Warn);
        }
    }

    public static void Send(Session session, object panel)
    {
        var text = JsonSerializer.Serialize(panel, Json);
        session.Network.EnqueueSend(new GameMessageSystemChat(text, (ChatMessageType)ChatType));
    }

    /// <summary>
    /// The skills panel: every trained or specialized skill with what the client
    /// already shows (base, current) beside what it cannot know - the Radiance points
    /// bought into it and what the next one costs. Rows the balance can afford are
    /// coloured, so the answer to "what can I raise right now" is visible at a glance.
    /// </summary>
    public static object SkillsPanel(Player player)
    {
        var balance = player.Account is null ? 0 : MasteryDb.RadianceBalance(player.Account.AccountId);
        var rows = new List<object>();

        foreach (var (skill, creatureSkill) in player.Skills.OrderBy(s => s.Key.ToSentence()))
        {
            if (creatureSkill.AdvancementClass < SkillAdvancementClass.Trained)
                continue;

            var ranks = Mastery.RanksOf(player, skill);
            var next = Mastery.CostOfNextRank(ranks, creatureSkill.AdvancementClass);
            var atCeiling = ranks >= Mod.Settings.MaxSkillPoints * Math.Max(1, Mod.Settings.RanksPerSkillPoint);

            rows.Add(new
            {
                k = skill.ToString().ToLowerInvariant(),
                c = new[]
                {
                    skill.ToSentence(),
                    creatureSkill.AdvancementClass == SkillAdvancementClass.Specialized ? "Spec" : "Trained",
                    creatureSkill.Base.ToString("N0"),
                    creatureSkill.Current.ToString("N0"),
                    Mastery.Points(ranks),
                    atCeiling ? "at ceiling" : next.ToString("N0"),
                },
                col = atCeiling ? "#A0A0A0" : next <= balance ? "#9BE39B" : null,
            });
        }

        return new
        {
            v = 1,
            id = "skills",
            title = "Aeshnidae - Skills",
            sub = $"Banked Radiance: {balance:N0}   -   one raise is {Mastery.RankWord()}, ceiling {Mod.Settings.MaxSkillPoints:N0} points",
            cols = new object[]
            {
                new { n = "Skill", w = 150 },
                new { n = "Training", w = 60 },
                new { n = "Base", w = 50 },
                new { n = "Current", w = 60 },
                new { n = "Radiance", w = 70 },
                new { n = "Next point", w = 0 },
            },
            rows,
            acts = new object[]
            {
                new { l = "Raise +1", c = "/raise {key} 1", row = true },
                new { l = "Raise +5", c = "/raise {key} 5", row = true },
                new { l = "Refresh", c = "/hud sync" },
            },
        };
    }

    [CommandHandler("hud", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Server-drawn panels, for clients that can show them (OpenAC with the Aeshnidae HUD plugin).",
        "/hud on|off|sync")]
    public static void HandleHud(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null)
            return;

        var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

        switch (verb)
        {
            case "on":
                TurnOn(session);
                Refresh(player);
                break;

            case "off":
                TurnOff(session);
                session.Network.EnqueueSend(new GameMessageSystemChat("HUD feed off.", ChatMessageType.Broadcast));
                break;

            case "sync":
                if (IsOn(session))
                    Refresh(player);
                else
                    session.Network.EnqueueSend(new GameMessageSystemChat("HUD feed is off - /hud on first.", ChatMessageType.Broadcast));
                break;

            default:
                session.Network.EnqueueSend(new GameMessageSystemChat(
                    $"HUD feed is {(IsOn(session) ? "on" : "off")}. It only does anything in a client that can draw the panels.",
                    ChatMessageType.Broadcast));
                break;
        }
    }
}
