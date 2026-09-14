namespace Aeshnidae.SkillMastery;

/// <summary>
/// The skills panel for the HUD feed.
///
/// The feed itself - the /hud switch, who is listening, the wire contract - is
/// Aeshnidae.Hud; this mod is one provider. HudFeed.cs is the verbatim copy of that
/// mod's Feed.cs (cross-mod types are unsafe, ACE's are not), and /hud-skills is the
/// command the Hud mod invokes when a session turns the feed on or asks for a sync.
/// </summary>
public static class Hud
{
    /// <summary>Re-send every panel this mod owns, if the session is listening.</summary>
    public static void Refresh(Player? player)
    {
        try
        {
            if (player?.Session is null || !HudFeed.IsOn(player))
                return;

            HudFeed.Send(player.Session, SkillsPanel(player));
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] hud refresh failed: {ex}", ModManager.LogLevel.Warn);
        }
    }

    /// <summary>
    /// Ask Aeshnidae.Hud to re-send every provider's panel - ours and the bank's, since a
    /// raise changes both. Goes through ACE's command registry because the Hud mod's
    /// types are out of reach; if that mod is not loaded, falls back to our own panel.
    /// </summary>
    public static void RefreshAll(Session session)
    {
        if (session.Player is null || !HudFeed.IsOn(session.Player))
            return;

        var hud = CommandManager.GetCommandByName("hud").FirstOrDefault();

        if (hud is null)
        {
            Refresh(session.Player);
            return;
        }

        try
        {
            ((CommandHandler)hud.Handler)(session, "sync");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] hud sync failed: {ex}", ModManager.LogLevel.Warn);
        }
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

    /// <summary>The provider command Aeshnidae.Hud invokes; a player can also type it.</summary>
    [CommandHandler("hud-skills", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Re-send the skills panel to a client showing the HUD feed.",
        "/hud-skills")]
    public static void HandleHudSkills(Session session, params string[] parameters) =>
        Refresh(session.Player);
}
