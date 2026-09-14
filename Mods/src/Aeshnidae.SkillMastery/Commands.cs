namespace Aeshnidae.SkillMastery;

public static class Commands
{
    [CommandHandler("mastery", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Skill mastery: progression past the retail cap that survives enlightenment.",
        "/mastery                     what you have\n" +
        "/mastery <skill>             what the next points cost\n" +
        "/mastery raise <skill> [n]   buy n points, default 1")]
    public static void HandleMastery(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null)
            return;

        if (parameters.Length > 0 && parameters[0].Equals("raise", StringComparison.OrdinalIgnoreCase))
        {
            HandleRaise(session, parameters.Skip(1).ToArray());
            return;
        }

        if (parameters.Length > 0)
        {
            ShowSkill(session, string.Join(" ", parameters));
            return;
        }

        ShowAll(session);
    }

    /// <summary>
    /// /cost and /x: the same readout as /mastery, under the name a player reaches for
    /// when the question is "what would this cost me". /m and /c were the first choices;
    /// the client keeps both (Monarch and Co-vassal chat), and never sends them to us.
    /// </summary>
    [CommandHandler("cost", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "What the next mastery points in a skill cost, and how many you can afford.",
        "/cost melee defense")]
    public static void HandleCost(Session session, params string[] parameters) =>
        HandleMastery(session, parameters);

    [CommandHandler("x", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Short form of /cost.",
        "/x melee defense")]
    public static void HandleCostShort(Session session, params string[] parameters) =>
        HandleMastery(session, parameters);

    [CommandHandler("raise", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 1,
        "Buy mastery points in a skill with Radiance from your account bank. One raise is one point.",
        "/raise <skill> [points]")]
    public static void HandleRaise(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null || parameters.Length == 0)
            return;

        var count = 1;
        var nameParts = parameters;

        if (parameters.Length > 1 && int.TryParse(parameters[^1], out var parsed))
        {
            count = parsed;
            nameParts = parameters[..^1];
        }

        if (!TryParseSkill(string.Join(" ", nameParts), out var skill))
        {
            Reply(session, $"No skill called '{string.Join(" ", nameParts)}'.");
            return;
        }

        var raised = Mastery.Raise(player, skill, count, out var message);
        Reply(session, message);

        // A listening client redraws every panel: the skills panel from the new ranks,
        // and the bank panel, since a raise spends Radiance.
        if (raised)
            Hud.RefreshAll(session);
    }

    private static void ShowAll(Session session)
    {
        var player = session.Player!;
        var rows = MasteryDb.Load(player.Guid.Full);

        var sb = new StringBuilder();
        sb.AppendLine($"Skill mastery - each raise is {Mastery.RankWord()}, " +
                      $"ceiling {Mod.Settings.MaxSkillPoints:N0} points.");

        var any = false;

        foreach (var (skill, skillRanks) in rows.OrderBy(r => r.Key.ToString()))
        {
            if (skillRanks <= 0)
                continue;

            any = true;
            sb.AppendLine($"  {skill.ToSentence()}: {Mastery.Points(skillRanks)} points (+{Mastery.SkillPointsFor(skillRanks):N0} on the skill)");
        }

        if (!any)
            sb.AppendLine("  nothing yet. /mastery <skill> to see what it costs.");

        sb.AppendLine($"Banked Radiance: {(player.Account is null ? 0 : MasteryDb.RadianceBalance(player.Account.AccountId)):N0}");

        Reply(session, sb.ToString().TrimEnd());
    }

    private static void ShowSkill(Session session, string name)
    {
        var player = session.Player!;

        if (!TryParseSkill(name, out var skill))
        {
            Reply(session, $"No skill called '{name}'.");
            return;
        }

        var creatureSkill = player.GetCreatureSkill(skill, false);

        if (creatureSkill is null)
        {
            Reply(session, $"You have no {skill.ToSentence()} skill.");
            return;
        }

        var ranks = Mastery.RanksOf(player, skill);
        var cls = creatureSkill.AdvancementClass;
        var balance = player.Account is null ? 0 : MasteryDb.RadianceBalance(player.Account.AccountId);

        var per = Math.Max(1, Mod.Settings.RanksPerSkillPoint);

        // How many raises the balance buys from here, priced one at a time up the curve.
        var affordable = 0;
        long spent = 0;
        while (affordable < 500 * per)
        {
            var next = Mastery.CostOfNextRank(ranks + affordable, cls);
            if (spent + next > balance) break;
            spent += next; affordable++;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{skill.ToSentence()} ({cls}) - {creatureSkill.Current:N0} now, " +
                      $"{Mastery.Points(ranks)} mastery points (+{Mastery.SkillPointsFor(ranks):N0} on the skill)");

        if (per > 1)
        {
            // One raise, the rest of the current point, and a whole point beyond that.
            var toWhole = per - ranks % per;
            sb.AppendLine($"  next raise : {Mastery.CostOfRanks(ranks, 1, cls):N0} Radiance ({Mastery.RankWord()})");
            sb.AppendLine($"  to {Mastery.Points(ranks + toWhole)} ({toWhole} raise{(toWhole == 1 ? "" : "s")}) : {Mastery.CostOfRanks(ranks, toWhole, cls):N0}");
            sb.AppendLine($"  to {Mastery.Points(ranks + toWhole + per)} ({toWhole + per} raises) : {Mastery.CostOfRanks(ranks, toWhole + per, cls):N0}");
        }
        else
        {
            sb.AppendLine($"  next +1    : {Mastery.CostOfRanks(ranks, 1, cls):N0} Radiance");
            sb.AppendLine($"  next +5    : {Mastery.CostOfRanks(ranks, 5, cls):N0}");
            sb.AppendLine($"  next +10   : {Mastery.CostOfRanks(ranks, 10, cls):N0}");
        }

        var unit = per > 1 ? "raise" : "point";
        sb.AppendLine($"  you have   : {balance:N0} Radiance - enough for {affordable:N0} {unit}{(affordable == 1 ? "" : "s")}" +
                      (per > 1 && affordable > 0 ? $" ({Mastery.Points(affordable)} points)" : "") +
                      (affordable > 0 ? $" costing {spent:N0}" : ""));
        sb.Append($"  /raise {skill.ToSentence().ToLowerInvariant()} {(affordable > 0 ? affordable : 1)}   to buy");

        Reply(session, sb.ToString());
    }

    private static bool TryParseSkill(string name, out Skill skill)
    {
        var cleaned = name.Replace(" ", "").Replace("_", "");

        foreach (Skill candidate in Enum.GetValues(typeof(Skill)))
        {
            if (candidate.ToString().Equals(cleaned, StringComparison.OrdinalIgnoreCase))
            {
                skill = candidate;
                return true;
            }
        }

        skill = Skill.None;
        return false;
    }

    /// <summary>
    /// One chat message per line, with any carriage return stripped.
    ///
    /// StringBuilder.AppendLine emits Environment.NewLine, which is CR LF on Windows.
    /// The client breaks the line on LF and renders the stray CR as an unmapped glyph -
    /// those are the musical notes. Every other mod here splits the same way; this is
    /// the house pattern, not a local workaround.
    /// </summary>
    private static void Reply(Session session, string message)
    {
        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                session.Network.EnqueueSend(new GameMessageSystemChat(text, ChatMessageType.Broadcast));
        }
    }
}
