namespace Aeshnidae.DevKit;

/// <summary>
/// The content developer's loop, as chat commands:
///
///   /winfo    what does this weenie do              (any server)
///   /wfind    which wcid is that                     (any server)
///   /wexport  give me its SQL                        (any server, to Discord)
///   /wimport  put my edited SQL in and make it live  (staging only)
///   /wdiff    what did I change                      (staging)
///   /lbdiff   what did I place or move               (staging)
///   /submit   package it for the change ledger       (staging, to Discord)
///
/// Every command works from the console too (CommandHandlerFlag.None), so the whole
/// loop can be driven and tested through Aeshnidae.RemoteConsole without a client.
/// The slow parts - the SQL writer's first build, database reads, Discord - run off
/// the world thread and reply through the player's action queue.
/// </summary>
public static class Commands
{
    // ---- inspect --------------------------------------------------------------------

    [CommandHandler("winfo", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Describe a weenie: what it gives, awards, sells, spawns and casts, then its properties.",
        "/winfo <wcid | classname> [full]\n" +
        "e.g. /winfo 22642        Brighteyes the Tailor, emotes decoded\n" +
        "     /winfo 22642 full   also palettes, textures and anim parts")]
    public static void HandleInfo(Session session, params string[] parameters)
    {
        var target = parameters[0].Trim();
        var full = parameters.Length > 1 && parameters[1].Equals("full", StringComparison.OrdinalIgnoreCase);

        Background(session, () =>
        {
            var weenie = Exporter.Find(target);
            if (weenie is null)
                return new[] { $"No weenie '{target}' on {Mod.Settings.Label}. Try /wfind <part of the name>." };

            return WeenieInfo.Render(weenie, full);
        });
    }

    [CommandHandler("wfind", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Find weenies by name. Every word given must appear in the name.",
        "/wfind <word> [word ...]\n" +
        "e.g. /wfind tusker tusk")]
    public static void HandleFind(Session session, params string[] parameters)
    {
        var words = parameters.Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        if (words.Length == 0)
        {
            Reply(session, "Usage: /wfind <word> [word ...]");
            return;
        }

        Background(session, () =>
        {
            var max = Mod.Settings.MaxFindResults;

            var hits = Names.Weenies
                .Where(kv => words.All(w => kv.Value.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(kv => kv.Key)
                .ToList();

            if (hits.Count == 0)
                return new[] { $"No weenie name contains {string.Join(" + ", words.Select(w => $"'{w}'"))}." };

            var lines = new List<string> { $"{hits.Count} match(es) for {string.Join(" + ", words)}:" };
            lines.AddRange(hits.Take(max).Select(kv => $"  {kv.Key,6}  {kv.Value}"));
            if (hits.Count > max)
                lines.Add($"  ... {hits.Count - max} more - add a word to narrow it.");
            return lines;
        });
    }

    // ---- export / import ------------------------------------------------------------------

    [CommandHandler("wexport", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Export a weenie's SQL to the developer Discord channel as a file.",
        "/wexport <wcid | classname>\n" +
        "e.g. /wexport 22642   or   /wexport ace22642-brighteyesthetailor")]
    public static void HandleExport(Session session, params string[] parameters)
    {
        var settings = Mod.Settings;
        if (!settings.HasWebhook)
        {
            Reply(session, $"{Mod.Name}: no Discord webhook configured - set WebhookUrl in Settings.json and /devkit-reload.");
            return;
        }

        var target = parameters[0].Trim();
        var who = Who(session);

        Reply(session, $"Exporting {target} to Discord…");

        Background(session, () =>
        {
            var export = Exporter.Export(target);
            if (export is null)
                return new[] { $"Couldn't find weenie '{target}'." };

            if (export.Sql.Length > settings.MaxAttachmentBytes)
                return new[] { $"{export.FileName} is {export.Sql.Length / 1024} KB, over the {settings.MaxAttachmentBytes / 1024} KB attachment limit." };

            var w = export.Weenie;
            var name = w.WeeniePropertiesString?.FirstOrDefault(s => s.Type == (ushort)PropertyString.Name)?.Value ?? w.ClassName;
            var message = $":package: **{name}**  `wcid {w.ClassId}` · {(WeenieType)w.Type} · {export.Lines} lines · from **{settings.Label}**\n" +
                          $"exported by {who}";

            var sent = DiscordUpload.SendFileAsync(settings.WebhookUrl, settings.Username, message, export.FileName, export.Sql).GetAwaiter().GetResult();

            ModManager.Log($"[{Mod.Name}] {who} exported {w.ClassId} {name} -> Discord: {(sent.Ok ? "ok" : sent.Detail)}");

            return new[]
            {
                sent.Ok
                    ? $"Sent {export.FileName} ({export.Sql.Length / 1024} KB) to Discord. Edit it, upload it to sql/weenies/ and /wimport {w.ClassId}."
                    : $"Discord refused {export.FileName}: {sent.Detail}",
            };
        });
    }

    [CommandHandler("lbexport", AccessLevel.Developer, CommandHandlerFlag.None, 0,
        "Export a landblock's placed instances as SQL to the developer Discord channel.",
        "/lbexport [landblock]\n" +
        "e.g. /lbexport A9B4      no argument = the landblock you are standing in")]
    public static void HandleLandblockExport(Session session, params string[] parameters)
    {
        var settings = Mod.Settings;
        if (!settings.HasWebhook)
        {
            Reply(session, $"{Mod.Name}: no Discord webhook configured.");
            return;
        }

        if (!TryLandblock(session, parameters.Length > 0 ? parameters[0] : "here", out var lb, out var error))
        {
            Reply(session, error);
            return;
        }

        var who = Who(session);

        Background(session, () =>
        {
            var export = Exporter.ExportLandblock(lb);
            if (export is null)
                return new[] { $"Landblock {lb:X4} has no placed instances." };

            var message = $":round_pushpin: **landblock {lb:X4}**  {export.Instances} instance(s) · from **{settings.Label}**\nexported by {who}";
            var sent = DiscordUpload.SendFileAsync(settings.WebhookUrl, settings.Username, message, export.FileName, export.Sql).GetAwaiter().GetResult();

            return new[] { sent.Ok ? $"Sent {export.FileName} ({export.Instances} instances) to Discord." : $"Discord refused: {sent.Detail}" };
        });
    }

    [CommandHandler("wimport", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Import an edited weenie .sql from the content folder's sql/weenies/ and make it live at once.",
        "/wimport <wcid | filename>\n" +
        "Reads <content_folder>/sql/weenies/<wcid> *.sql (as /import-sql does), then reloads the\n" +
        "weenie and re-points its live instances (as /clearweenie does), then shows what changed\n" +
        "against live. Staging only - refused where AllowImport is off.")]
    public static void HandleImport(Session session, params string[] parameters)
    {
        var settings = Mod.Settings;
        if (!settings.AllowImport)
        {
            Reply(session, $"Imports are OFF on {settings.Label}. Changes to the live world go through the change ledger: /submit from staging.");
            return;
        }

        var target = parameters[0].Trim();
        var who = Who(session);

        Background(session, () =>
        {
            var folder = WeenieSqlFolder();
            if (!Directory.Exists(folder))
                return new[] { $"No {folder} - is content_folder set? (/modifystring content_folder <path>)" };

            var matches = FindImportFiles(folder, target);
            if (matches.Count == 0)
                return new[] { $"Nothing in {folder} matches '{target}'. Files are named '<wcid> <name>.sql' - upload yours there first." };
            if (matches.Count > 1)
                return new[] { $"'{target}' matches {matches.Count} files - say which:" }.Concat(matches.Select(f => "  " + Path.GetFileName(f))).ToArray();

            var file = matches[0];
            var m = Regex.Match(Path.GetFileName(file), @"\d+");
            if (!m.Success || !uint.TryParse(m.Value, out var wcid))
                return new[] { $"Can't tell the wcid from '{Path.GetFileName(file)}' - name it '<wcid> <name>.sql'." };

            // ACE's own importer: runs the file against the world db as one batch.
            try
            {
                ACE.Server.Command.Handlers.Processors.DeveloperContentCommands.ImportSQL(file);
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] /wimport {file} by {who} failed: {ex}", ModManager.LogLevel.Error);
                return new[] { $"Import FAILED, nothing changed: {Root(ex).Message}" };
            }

            ModManager.Log($"[{Mod.Name}] {who} imported {Path.GetFileName(file)} on {settings.Label}");

            var lines = new List<string> { $"Imported {Path.GetFileName(file)}." };

            // Make it live: evict from ACE's cache, then let Aeshnidae.ClearWeenie re-point
            // the instances already standing in the world. Dispatched by name so this mod
            // does not depend on that one's types; if it is not loaded, the eviction alone
            // means the next spawn is right, and we say so.
            DatabaseManager.World.ClearCachedWeenie(wcid);
            lines.Add(RunOnWorld(session, $"clearweenie {wcid}")
                ? $"Reloaded {wcid} and refreshed its live instances."
                : $"Reloaded {wcid}. (Aeshnidae.ClearWeenie is not loaded, so instances already in the world keep the old definition until they respawn.)");

            if (settings.HasBase)
            {
                var diff = Diff.Weenie(wcid);
                lines.Add(diff.Changed ? $"Against live, {diff.Subject}:" : $"{diff.Subject} is now identical to live.");
                lines.AddRange(Cap(diff.Lines, settings.MaxDiffLines));
            }

            return lines;
        });
    }

    // ---- diff -----------------------------------------------------------------------------

    [CommandHandler("wdiff", AccessLevel.Developer, CommandHandlerFlag.None, 1,
        "Show how a weenie on this server differs from live.",
        "/wdiff <wcid> [wcid ...]\n" +
        "~ changed   - removed   + added.  Compares against the base copy of live this server was cloned from.")]
    public static void HandleDiff(Session session, params string[] parameters)
    {
        if (!RequireBase(session)) return;

        var wcids = ParseWcids(parameters, out var bad);
        if (bad.Count > 0)
            Reply(session, $"Not a wcid: {string.Join(", ", bad)}");
        if (wcids.Count == 0)
            return;

        Background(session, () =>
        {
            var lines = new List<string>();
            foreach (var wcid in wcids)
            {
                var diff = Diff.Weenie(wcid);
                if (!diff.InBase && !diff.InWorld)
                    lines.Add($"{wcid}: no such weenie on either side.");
                else if (!diff.Changed)
                    lines.Add($"{diff.Subject}: identical to live.");
                else
                {
                    lines.Add($"{diff.Subject}: {diff.Lines.Count} change(s) against live");
                    lines.AddRange(Cap(diff.Lines, Mod.Settings.MaxDiffLines));
                }
            }
            return lines;
        });
    }

    [CommandHandler("lbdiff", AccessLevel.Developer, CommandHandlerFlag.None, 0,
        "Show how a landblock's placed instances differ from live.",
        "/lbdiff [landblock]\n" +
        "e.g. /lbdiff A9B4      no argument = the landblock you are standing in")]
    public static void HandleLandblockDiff(Session session, params string[] parameters)
    {
        if (!RequireBase(session)) return;

        if (!TryLandblock(session, parameters.Length > 0 ? parameters[0] : "here", out var lb, out var error))
        {
            Reply(session, error);
            return;
        }

        Background(session, () =>
        {
            var diff = Diff.Landblock(lb);
            if (!diff.InBase && !diff.InWorld)
                return new[] { $"Landblock {lb:X4} has no placed instances on either side." };
            if (!diff.Changed)
                return new[] { $"Landblock {lb:X4}: identical to live." };

            var lines = new List<string> { $"Landblock {lb:X4}: {diff.Lines.Count} change(s) against live" };
            lines.AddRange(Cap(diff.Lines, Mod.Settings.MaxDiffLines));
            return lines;
        });
    }

    // ---- submit ---------------------------------------------------------------------------

    [CommandHandler("submit", AccessLevel.Developer, CommandHandlerFlag.None, 2,
        "Package changed weenies and landblocks as a change file + patch note for the ledger, and post them to Discord.",
        "/submit <slug> <wcid | lb:XXXX | lb:here> [...] [-- why it changed]\n" +
        "e.g. /submit tusk-xp-halved 22419 22420 -- 95M for one turn-in outpaced every other reward\n" +
        "     /submit holtburg-font-portal 90301 lb:A9B4 -- a second door to the Font, beside the buff bot\n" +
        "The slug is lower-case words joined by dashes; it becomes the file name and the title.")]
    public static void HandleSubmit(Session session, params string[] parameters)
    {
        var settings = Mod.Settings;

        var slug = parameters[0].Trim().ToLowerInvariant();
        if (!Submit.ValidSlug(slug))
        {
            Reply(session, $"'{slug}' is not a slug: lower-case words joined by dashes, e.g. tusk-xp-halved.");
            return;
        }

        var wcids = new List<uint>();
        var landblocks = new List<ushort>();
        var why = "";

        for (var i = 1; i < parameters.Length; i++)
        {
            var p = parameters[i].Trim();

            if (p == "--")
            {
                why = string.Join(' ', parameters.Skip(i + 1)).Trim();
                break;
            }

            if (p.StartsWith("lb:", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryLandblock(session, p[3..], out var lb, out var error))
                {
                    Reply(session, error);
                    return;
                }
                if (!landblocks.Contains(lb)) landblocks.Add(lb);
                continue;
            }

            if (uint.TryParse(p, out var wcid) && wcid > 0)
            {
                if (!wcids.Contains(wcid)) wcids.Add(wcid);
                continue;
            }

            Reply(session, $"'{p}' is neither a wcid nor lb:XXXX. Put the reason after ' -- '.");
            return;
        }

        if (wcids.Count == 0 && landblocks.Count == 0)
        {
            Reply(session, "Nothing to submit: list at least one wcid or lb:XXXX.");
            return;
        }

        var author = Who(session);
        Reply(session, $"Building {slug}: {wcids.Count} weenie(s), {landblocks.Count} landblock(s)…");

        Background(session, () =>
        {
            var output = Submit.Build(new Submit.Request(slug, wcids, landblocks, why, author));
            var (sqlPath, notePath) = Submit.Save(output);

            var lines = new List<string>();
            lines.AddRange(output.Summary.Select(s => "  " + s));
            lines.AddRange(output.Warnings.Select(w => "  ! " + w));
            lines.Add($"Saved {Path.GetFileName(sqlPath)} and {Path.GetFileName(notePath)} in {Path.GetDirectoryName(sqlPath)}.");

            ModManager.Log($"[{Mod.Name}] {author} submitted {slug} ({string.Join(",", wcids)}; {string.Join(",", landblocks.Select(l => l.ToString("X4")))}) -> {sqlPath}");

            if (!settings.HasWebhook)
            {
                lines.Add("No Discord webhook configured - the files are on disk only.");
                return lines;
            }

            var message = new StringBuilder()
                .AppendLine($":inbox_tray: **Submission: {slug}** from **{settings.Label}** by {author}")
                .AppendLine(string.Join("\n", output.Summary.Select(s => "• " + s)));
            if (output.Warnings.Count > 0)
                message.AppendLine(string.Join("\n", output.Warnings.Select(w => ":warning: " + w)));
            if (!string.IsNullOrWhiteSpace(why))
                message.AppendLine($"**Why:** {why}");
            message.Append("To apply: fill in the patch note, rename `NNN` to the day's next number, drop both in `Mods/Content/sql/changes/`, `push-content.sh`.");

            var files = new[]
            {
                new DiscordUpload.Attachment(Path.GetFileName(sqlPath), output.ChangeSql),
                new DiscordUpload.Attachment(Path.GetFileName(notePath), output.PatchNote),
            };

            var total = output.ChangeSql.Length + output.PatchNote.Length;
            if (total > settings.MaxAttachmentBytes)
            {
                lines.Add($"Too big for Discord ({total / 1024} KB) - files are on disk only.");
                return lines;
            }

            var sent = DiscordUpload.SendFilesAsync(settings.WebhookUrl, settings.Username, message.ToString(), files).GetAwaiter().GetResult();
            lines.Add(sent.Ok ? "Posted to Discord for review." : $"Discord refused the post: {sent.Detail} - the files are still on disk.");
            return lines;
        });
    }

    // ---- status -------------------------------------------------------------------------

    [CommandHandler("devkit", AccessLevel.Developer, CommandHandlerFlag.None, 0,
        "Show Aeshnidae.DevKit status and its commands.",
        "/devkit")]
    public static void HandleStatus(Session session, params string[] parameters)
    {
        var container = Mod.Container;
        var s = Mod.Settings;

        Reply(session, new StringBuilder()
            .AppendLine($"{Mod.Name} v{container?.Meta.Version ?? "?"} - {container?.Status.ToString() ?? "not loaded"} - this is **{s.Label}**")
            .AppendLine($"Imports: {(s.AllowImport ? "ALLOWED" : "off")}   Base schema: {(s.HasBase ? s.BaseSchema : "(none - no diff/submit)")}   Db: {(Db.Ready ? Db.WorldSchema : "not connected")}")
            .AppendLine($"Webhook: {s.MaskedWebhook}   Max attachment: {s.MaxAttachmentBytes / 1024} KB   Submissions: {(s.HasBase || s.AllowImport ? Submit.Directory() : "n/a")}")
            .AppendLine("Inspect: /winfo <wcid> [full]   /wfind <words>")
            .AppendLine("Export:  /wexport <wcid>   /lbexport [landblock]")
            .AppendLine("Change:  /wimport <wcid|file>   /wdiff <wcid...>   /lbdiff [landblock]")
            .AppendLine("Ship:    /submit <slug> <wcid|lb:XXXX ...> -- why")
            .ToString());
    }

    [CommandHandler("devkit-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart Aeshnidae.DevKit, re-reading Settings.json.",
        "/devkit-reload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;
        if (container is null)
        {
            Reply(session, $"{Mod.Name} is not loaded.");
            return;
        }

        container.Restart();
        Reply(session, $"{Mod.Name} restarted.");
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static bool RequireBase(Session session)
    {
        if (!Db.Ready)
        {
            Reply(session, $"{Mod.Name}: no world database connection (see the log).");
            return false;
        }
        if (!Mod.Settings.HasBase)
        {
            Reply(session, $"No base schema on {Mod.Settings.Label} - there is nothing to diff against here. Diff and submit from staging.");
            return false;
        }
        if (!Db.SchemaExists(Mod.Settings.BaseSchema))
        {
            Reply(session, $"Base schema '{Mod.Settings.BaseSchema}' does not exist - has refresh-staging.sh been run?");
            return false;
        }
        return true;
    }

    private static List<uint> ParseWcids(string[] parameters, out List<string> bad)
    {
        var wcids = new List<uint>();
        bad = new List<string>();
        foreach (var raw in parameters)
            foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (uint.TryParse(token, out var wcid) && wcid > 0) { if (!wcids.Contains(wcid)) wcids.Add(wcid); }
                else bad.Add(token);
            }
        return wcids;
    }

    /// <summary>"A9B4", "0xA9B4", "0xA9B40019" (a cell id) or "here" (the player's landblock).</summary>
    private static bool TryLandblock(Session? session, string text, out ushort landblock, out string error)
    {
        landblock = 0;
        error = "";
        text = text.Trim();

        if (text.Equals("here", StringComparison.OrdinalIgnoreCase))
        {
            var lb = session?.Player?.CurrentLandblock;
            if (lb is null)
            {
                error = "'here' needs a player in the world - give the landblock, e.g. A9B4.";
                return false;
            }
            landblock = lb.Id.Landblock;
            return true;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];

        if (!uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value) || value == 0)
        {
            error = $"'{text}' is not a landblock. Use four hex digits, e.g. A9B4, or 'here'.";
            return false;
        }

        landblock = value > 0xFFFF ? (ushort)(value >> 16) : (ushort)value;
        return true;
    }

    private static string WeenieSqlFolder()
    {
        var content = PropertyManager.GetString("content_folder").Item ?? "Content";
        if (content.StartsWith('.'))
            content = Path.Combine(Environment.CurrentDirectory, content);
        return Path.Combine(content, "sql", "weenies");
    }

    private static List<string> FindImportFiles(string folder, string target)
    {
        var files = Directory.GetFiles(folder, "*.sql");

        if (uint.TryParse(target, out var wcid))
        {
            // '<wcid> name.sql', with or without ACE's zero padding, is the file for this wcid.
            var rx = new Regex($@"^0*{wcid}(?!\d)", RegexOptions.IgnoreCase);
            return files.Where(f => rx.IsMatch(Path.GetFileName(f))).OrderBy(f => f).ToList();
        }

        var name = target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? target : target + ".sql";
        return files.Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>
    /// Runs a console-style command line on the world thread when there is a player to
    /// hang it off, or inline otherwise. True if the command exists and did not throw.
    /// </summary>
    private static bool RunOnWorld(Session? session, string commandLine)
    {
        try
        {
            CommandManager.ParseCommand(commandLine, out var command, out var parameters);
            var response = CommandManager.GetCommandHandler(null, command, parameters, out var info);
            if (response != CommandHandlerResponse.Ok || info is null)
                return false;

            var handler = (CommandHandler)info.Handler;
            var player = session?.Player;

            if (player is null)
            {
                handler.Invoke(null, parameters);
                return true;
            }

            var done = new ManualResetEventSlim(false);
            var ok = false;
            var chain = new ActionChain();
            chain.AddAction(player, () =>
            {
                try { handler.Invoke(null, parameters); ok = true; }
                catch (Exception ex) { ModManager.Log($"[{Mod.Name}] '{commandLine}' threw: {ex}", ModManager.LogLevel.Error); }
                finally { done.Set(); }
            });
            chain.EnqueueChain();
            done.Wait(TimeSpan.FromSeconds(30));
            return ok;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] '{commandLine}' failed: {ex}", ModManager.LogLevel.Error);
            return false;
        }
    }

    private static IEnumerable<string> Cap(List<string> lines, int max)
    {
        if (lines.Count <= max) return lines.Select(l => "  " + l);
        return lines.Take(max).Select(l => "  " + l).Append($"  ... {lines.Count - max} more line(s); /submit includes them all.");
    }

    private static Exception Root(Exception ex)
    {
        while (ex.InnerException is not null) ex = ex.InnerException;
        return ex;
    }

    private static string Who(Session? session) => session?.Player?.Name ?? session?.Account ?? "console";

    /// <summary>
    /// Run work off the world thread and deliver its lines afterwards - through the
    /// player's action queue when there is a player, to the log for the console.
    /// A throw becomes one line rather than a dead command.
    /// </summary>
    private static void Background(Session? session, Func<IEnumerable<string>> work)
    {
        _ = Task.Run(() =>
        {
            IEnumerable<string> lines;
            try
            {
                lines = work();
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] command failed: {ex}", ModManager.LogLevel.Error);
                lines = new[] { $"Failed: {Root(ex).Message}" };
            }

            var text = string.Join("\n", lines);
            var player = session?.Player;

            if (player is null)
            {
                Reply(session, text);
                return;
            }

            var chain = new ActionChain();
            chain.AddAction(player, () => Reply(session, text));
            chain.EnqueueChain();
        });
    }

    /// <summary>The client renders an embedded newline as a music note, so one send per line.</summary>
    private static void Reply(Session? session, string message)
    {
        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(text))
                continue;

            if (session?.Player is null)
                ModManager.Log($"[{Mod.Name}] {text}");
            else
                session.Player.SendMessage(text);
        }
    }
}
