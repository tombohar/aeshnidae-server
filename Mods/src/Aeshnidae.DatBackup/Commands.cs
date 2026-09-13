namespace Aeshnidae.DatBackup;

/// <summary>
/// Admin commands for the dat snapshot store.
///
/// One command with subcommands rather than five separate ones, because they are
/// only ever used together and a stray "/datrestore" in a chat window should not be
/// one keystroke from replacing a gigabyte of game data.
/// </summary>
public static class Commands
{
    [CommandHandler("datbackup", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Snapshot, inspect and restore the server's dat files.",
        "/datbackup            - take a snapshot now\n" +
        "/datbackup list       - what is stored\n" +
        "/datbackup verify [id]- re-hash a snapshot's stored copies (newest if no id)\n" +
        "/datbackup restore <id> - put a snapshot back (server must not hold the dats)")]
    public static void HandleDatBackup(Session session, params string[] parameters)
    {
        var sub = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "take";
        var store = Mod.Settings.ResolvedBackupDirectory;

        switch (sub)
        {
            case "take": Take(session, store); break;
            case "list": ListAll(session, store); break;
            case "verify": Verify(session, store, parameters.Skip(1).FirstOrDefault()); break;
            case "restore": Restore(session, store, parameters.Skip(1).FirstOrDefault()); break;
            default: Reply(session, $"Unknown subcommand '{sub}'. Try list, verify or restore."); break;
        }
    }

    private static void Take(Session session, string store)
    {
        var source = Mod.Settings.ResolvedDatDirectory;
        Reply(session, $"Snapshotting {source} ...");

        var (snapshot, message) = SnapshotStore.Take(
            store, source, "manual", Mod.Settings.KeepSnapshots,
            m => ModManager.Log($"[{Mod.Name}] {m}"));

        Reply(session, snapshot is null ? $"Failed: {message}" : message);
    }

    private static void ListAll(Session session, string store)
    {
        var all = SnapshotStore.List(store);

        if (all.Count == 0)
        {
            Reply(session, $"No snapshots in {store}.");
            return;
        }

        var text = new StringBuilder()
            .AppendLine($"{all.Count} snapshot(s) in {store}, using {SnapshotStore.StoreSize(store) / 1024 / 1024:N0} MB on disk:");

        foreach (var s in all)
            text.AppendLine($"  {s.Id}  {s.TakenUtc.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                            $"{s.Files.Count} file(s), {s.TotalSize / 1024 / 1024:N0} MB  [{s.Label}]");

        Reply(session, text.ToString());
    }

    private static void Verify(Session session, string store, string? id)
    {
        var snapshot = id is null ? SnapshotStore.Latest(store) : SnapshotStore.Find(store, id);

        if (snapshot is null)
        {
            Reply(session, id is null ? "There are no snapshots to verify." : $"No snapshot '{id}'.");
            return;
        }

        Reply(session, $"Verifying {snapshot.Id} - this re-reads every stored copy ...");

        var (ok, problems) = SnapshotStore.Verify(store, snapshot);

        if (ok)
        {
            Reply(session, $"Snapshot {snapshot.Id} is intact - {snapshot.Files.Count} file(s) match their hashes.");
            return;
        }

        var text = new StringBuilder().AppendLine($"Snapshot {snapshot.Id} has problems:");
        foreach (var p in problems) text.AppendLine($"  {p}");
        Reply(session, text.ToString());
    }

    private static void Restore(Session session, string store, string? id)
    {
        if (id is null)
        {
            Reply(session, "Name the snapshot to restore - /datbackup list shows the ids.");
            return;
        }

        var snapshot = SnapshotStore.Find(store, id);

        if (snapshot is null)
        {
            Reply(session, $"No snapshot '{id}'.");
            return;
        }

        // Restore into wherever the snapshot came from. Restoring a server dat set
        // into a client profile, or the reverse, is never what anyone means.
        var destination = snapshot.Source;

        var (ok, message) = SnapshotStore.Restore(
            store, snapshot, destination, m => ModManager.Log($"[{Mod.Name}] {m}"));

        Reply(session, message);

        if (ok)
            Reply(session, "Restart the server so it reads the restored dats.");
    }

    [CommandHandler("datbackup-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart the Aeshnidae.DatBackup mod, re-reading Settings.json.",
        "/datbackup-reload")]
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

    /// <summary>
    /// Console and in-game both reach these commands, and a null session is how the
    /// console arrives - so every reply goes through here.
    ///
    /// One chat message per line, with any carriage return stripped:
    /// StringBuilder.AppendLine emits Environment.NewLine, which is CR LF on Windows.
    /// The client breaks the line on LF and renders the stray CR as an unmapped glyph -
    /// those are the musical notes. Matches every other mod here.
    /// </summary>
    private static void Reply(Session? session, string message)
    {
        if (session?.Player is null)
        {
            Console.WriteLine(message);
            return;
        }

        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                session.Player.SendMessage(text);
        }
    }
}
