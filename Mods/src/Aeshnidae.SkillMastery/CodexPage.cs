namespace Aeshnidae.SkillMastery;

/// <summary>
/// Skill mastery's pages in the Aeshnidae Codex. Found by Aeshnidae.Codex through
/// reflection (a static class called CodexPage with Title and Pages(Player)), so
/// nothing here references that mod. Ragged text on purpose: the book font is
/// proportional.
/// </summary>
public static class CodexPage
{
    public static string Title => "Skill Mastery";

    private const int SkillsPerPage = 14;

    public static string[] Pages(Player player)
    {
        if (player is null || !MasteryDb.Ready)
            return Array.Empty<string>();

        var rows = MasteryDb.Load(player.Guid.Full)
            .Where(r => r.Value > 0)
            .OrderBy(r => r.Key.ToString())
            .ToList();

        var banked = player.Account is null ? 0 : MasteryDb.RadianceBalance(player.Account.AccountId);

        var pages = new List<string>();
        var first = new StringBuilder();

        first.AppendLine("SKILL MASTERY");
        first.AppendLine();
        first.AppendLine($"Skills past the retail cap, bought with Radiance. Each raise is {Mastery.RankWord()}; the ceiling is {Mod.Settings.MaxSkillPoints:N0} points a skill.");
        first.AppendLine();
        first.AppendLine($"Banked Radiance: {banked:N0}");
        first.AppendLine();

        if (rows.Count == 0)
        {
            first.AppendLine("Nothing mastered yet.");
            first.AppendLine();
            first.AppendLine("/x <skill> shows what the next points cost; /raise <skill> [n] buys them.");
            pages.Add(first.ToString().TrimEnd());
            return pages.ToArray();
        }

        first.AppendLine($"{rows.Count} skill{(rows.Count == 1 ? "" : "s")} mastered, {rows.Sum(r => Mastery.SkillPointsFor(r.Value)):N0} points in all.");
        first.AppendLine("Each skill: points held, then what the next point costs.");
        pages.Add(first.ToString().TrimEnd());

        for (var start = 0; start < rows.Count; start += SkillsPerPage)
        {
            var sb = new StringBuilder();
            sb.AppendLine("MASTERED SKILLS");
            sb.AppendLine();

            foreach (var (skill, ranks) in rows.Skip(start).Take(SkillsPerPage))
            {
                var creatureSkill = player.GetCreatureSkill(skill, false);
                var advancement = creatureSkill?.AdvancementClass ?? SkillAdvancementClass.Trained;
                var next = Mastery.CostOfNextRank(ranks, advancement);
                var dormant = creatureSkill is null || creatureSkill.AdvancementClass < SkillAdvancementClass.Trained;

                sb.AppendLine($"{skill.ToSentence()}: +{Mastery.Points(ranks)}{(dormant ? " (banked - not trained)" : "")}");
                sb.AppendLine($"  next point {next:N0}");
            }

            pages.Add(sb.ToString().TrimEnd());
        }

        return pages.ToArray();
    }
}
