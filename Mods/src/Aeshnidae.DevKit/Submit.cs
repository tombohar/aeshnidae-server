namespace Aeshnidae.DevKit;

/// <summary>
/// /submit: turns what a developer changed on staging into the two files the change
/// ledger wants - a change .sql in the _template.sql shape and a .patchnote.md
/// skeleton - and hands them to the person who applies changes, via Discord.
///
/// The change file replaces each weenie whole (DELETE + INSERT, exactly what
/// /export-sql writes and what the ledger's before/ snapshots are), with the
/// difference against live written into its header as comments so a reviewer can see
/// what the replacement actually changes without diffing 400 lines of INSERT.
///
/// Nothing here touches the live server. The files are saved beside the content
/// folder and posted to #content; applying them is a person's decision, made with
/// push-content.sh, which snapshots, applies, refreshes the cache and tells players.
/// </summary>
internal static class Submit
{
    public sealed record Request(string Slug, List<uint> Wcids, List<ushort> Landblocks, string Why, string Author);

    public sealed record Output(string BaseName, byte[] ChangeSql, byte[] PatchNote, List<string> Summary, List<string> Warnings);

    private static readonly Regex SlugRx = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static bool ValidSlug(string slug) => slug.Length is >= 3 and <= 60 && SlugRx.IsMatch(slug);

    public static Output Build(Request req)
    {
        var settings = Mod.Settings;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var title = Title(req.Slug);
        var summary = new List<string>();
        var warnings = new List<string>();

        var header = new StringBuilder();
        var body = new StringBuilder();
        var noteValues = new StringBuilder();

        header.AppendLine($"-- Title:  {title}");
        header.AppendLine($"-- Wcids:  {string.Join(", ", req.Wcids)}");
        if (req.Landblocks.Count > 0)
            header.AppendLine($"-- Landblocks: {string.Join(", ", req.Landblocks.Select(l => l.ToString("X4")))}");
        header.AppendLine($"-- Why:    {(string.IsNullOrWhiteSpace(req.Why) ? "<TODO - the submitter did not say>" : Indent(req.Why))}");
        header.AppendLine($"-- Author: {req.Author} (via /submit on {settings.Label})");
        header.AppendLine($"-- Date:   {today}");
        header.AppendLine("--");
        header.AppendLine("-- HOW. Submitted from staging with /submit. Each weenie below is a full replacement");
        header.AppendLine("-- (DELETE + INSERT, as /export-sql writes it), which is also the shape of the");
        header.AppendLine("-- before/ snapshot the apply script takes. What that replacement changes against");
        header.AppendLine($"-- live, as of this submission{(settings.HasBase ? "" : " (NO BASE SCHEMA - diff unavailable)")}:");

        foreach (var wcid in req.Wcids)
        {
            var export = Exporter.Export(wcid.ToString());
            if (export is null)
            {
                warnings.Add($"{wcid}: no such weenie on {settings.Label} - skipped");
                continue;
            }

            var name = Names.Weenie(wcid);
            header.AppendLine($"--   {wcid} {name}:");

            if (settings.HasBase)
            {
                var diff = Diff.Weenie(wcid);
                if (!diff.Changed)
                {
                    warnings.Add($"{wcid} {name} is identical to live - included anyway, but the change file will replace it with itself");
                    header.AppendLine("--     (identical to live)");
                }
                foreach (var line in diff.Lines)
                {
                    header.AppendLine($"--     {line}");
                    noteValues.AppendLine(line);
                }
                summary.Add($"{wcid} {name}: {diff.Lines.Count} change line(s)");
            }
            else
            {
                summary.Add($"{wcid} {name}: exported ({export.Lines} lines)");
            }

            body.AppendLine();
            body.AppendLine($"-- ---- {wcid} {name} ------------------------------------------------------------");
            body.AppendLine(Exporter.Utf8NoBom.GetString(export.Sql).TrimEnd());
        }

        foreach (var lb in req.Landblocks)
        {
            var export = Exporter.ExportLandblock(lb);
            header.AppendLine($"--   landblock {lb:X4}:");

            if (settings.HasBase)
            {
                var diff = Diff.Landblock(lb);
                if (!diff.Changed)
                {
                    warnings.Add($"landblock {lb:X4} is identical to live - included anyway");
                    header.AppendLine("--     (identical to live)");
                }
                foreach (var line in diff.Lines)
                {
                    header.AppendLine($"--     {line}");
                    noteValues.AppendLine(line);
                }
                summary.Add($"landblock {lb:X4}: {diff.Lines.Count} change line(s), {export?.Instances ?? 0} instance(s) in total");
            }
            else
            {
                summary.Add($"landblock {lb:X4}: {export?.Instances ?? 0} instance(s)");
            }

            body.AppendLine();
            body.AppendLine($"-- ---- landblock {lb:X4} ({export?.Instances ?? 0} instances) -------------------------------------");
            if (export is null)
            {
                // The landblock has no instances at all now: the change is "remove them all".
                body.AppendLine($"DELETE FROM `landblock_instance` WHERE `landblock` = 0x{lb:X4};");
            }
            else
            {
                body.AppendLine(Exporter.Utf8NoBom.GetString(export.Sql).TrimEnd());
            }
        }

        header.AppendLine("--");
        header.AppendLine("-- Everything below runs in one transaction. Afterwards: /clearweenie <the wcids above>;");
        header.AppendLine("-- landblock placements need /reload-landblock while standing in the landblock, or");
        header.AppendLine("-- wait for it to unload.");
        header.AppendLine();
        header.AppendLine("USE aeshnidae_world;");

        var sql = header.ToString() + body.ToString().TrimEnd() + "\n";

        var note = new StringBuilder();
        note.AppendLine($"**{title}**");
        note.AppendLine();
        note.AppendLine("<What a player will notice, in one paragraph. Name the NPCs and items as they appear in game.>");
        note.AppendLine();
        note.AppendLine($"**Why:** {(string.IsNullOrWhiteSpace(req.Why) ? "<the reason, in a sentence or two>" : req.Why.Trim())}");
        note.AppendLine();
        note.AppendLine("**New values** (previous in brackets):");
        note.AppendLine("```");
        note.Append(noteValues.Length > 0 ? noteValues.ToString() : "<value   new   (old)>\n");
        note.AppendLine("```");
        note.AppendLine();
        note.AppendLine("**Unchanged:** <anything nearby that was deliberately left alone, so nobody asks>");

        var baseName = $"{today}_NNN_{req.Slug}";

        return new Output(baseName,
            Exporter.Utf8NoBom.GetBytes(sql),
            Exporter.Utf8NoBom.GetBytes(note.ToString()),
            summary, warnings);
    }

    /// <summary>Where submissions are kept on disk: Settings.SubmitDirectory, else &lt;content_folder&gt;/submitted.</summary>
    public static string Directory()
    {
        var dir = Mod.Settings.SubmitDirectory;

        if (string.IsNullOrWhiteSpace(dir))
        {
            var content = PropertyManager.GetString("content_folder").Item ?? "Content";
            if (content.StartsWith('.'))
                content = Path.Combine(Environment.CurrentDirectory, content);
            dir = Path.Combine(content, "submitted");
        }

        System.IO.Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Saves both files; returns the paths. A name clash gets a numeric suffix rather than an overwrite.</summary>
    public static (string sql, string note) Save(Output output)
    {
        var dir = Directory();
        var name = output.BaseName;
        var n = 1;
        while (File.Exists(Path.Combine(dir, name + ".sql")))
            name = $"{output.BaseName}-{++n}";

        var sqlPath = Path.Combine(dir, name + ".sql");
        var notePath = Path.Combine(dir, name + ".patchnote.md");
        File.WriteAllBytes(sqlPath, output.ChangeSql);
        File.WriteAllBytes(notePath, output.PatchNote);
        return (sqlPath, notePath);
    }

    private static string Title(string slug)
    {
        var words = slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    private static string Indent(string text) =>
        string.Join("\n--         ", text.Trim().Split('\n').Select(l => l.TrimEnd()));
}
