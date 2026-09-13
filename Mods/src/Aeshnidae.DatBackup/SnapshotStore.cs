using System.Security.Cryptography;

namespace Aeshnidae.DatBackup;

/// <summary>
/// Snapshots of a folder of dat files, stored content-addressed.
///
/// The shape is chosen by what dats actually are: four files, 1.4 GB together, that
/// change rarely and never partially. Copying all of them per snapshot would cost a
/// gigabyte to record that nothing happened, so each distinct file is stored once
/// under its own SHA-256 and a snapshot is just a manifest naming the hashes it
/// wants. A snapshot of an unchanged set therefore costs a few hundred bytes, which
/// is what makes it reasonable to take one on every server start.
///
/// Restoring is a copy out of the blob store, never a rename, so the store keeps
/// every generation it has been asked to keep even after a restore.
///
/// Deliberately plain BCL - no ACE types, no logging framework, no settings object -
/// because the launcher links this same file to snapshot client dat profiles. The
/// caller supplies a logger and the policy.
/// </summary>
public static class SnapshotStore
{
    /// <summary>What counts as a dat set. Nothing else in the folder is recorded.</summary>
    public const string DatPattern = "*.dat";

    public sealed class FileRecord
    {
        public string Name { get; set; } = "";
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public DateTime ModifiedUtc { get; set; }
    }

    public sealed class Snapshot
    {
        public string Id { get; set; } = "";
        public DateTime TakenUtc { get; set; }
        public string Source { get; set; } = "";
        public string Label { get; set; } = "";
        public List<FileRecord> Files { get; set; } = new();

        public long TotalSize => Files.Sum(f => f.Size);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string BlobDir(string store) => Path.Combine(store, "blobs");

    private static string SnapshotDir(string store) => Path.Combine(store, "snapshots");

    /// <summary>
    /// Blobs are bucketed by the first two hex characters. One flat directory of a
    /// few thousand entries is fine on NTFS; the buckets cost nothing and keep it
    /// browsable by hand, which matters when someone is recovering a dat at 2am.
    /// </summary>
    private static string BlobPath(string store, string hash) =>
        Path.Combine(BlobDir(store), hash[..2], hash);

    // ------------------------------------------------------------------ taking

    /// <summary>
    /// Records the current contents of <paramref name="source"/>. Returns the
    /// snapshot, or null with a reason if there was nothing to record.
    /// </summary>
    public static (Snapshot? Snapshot, string Message) Take(
        string store, string source, string label, int keep, Action<string>? log = null)
    {
        log ??= _ => { };

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            return (null, $"nothing to snapshot - {source} does not exist");

        var dats = Directory.GetFiles(source, DatPattern).OrderBy(f => f).ToList();

        if (dats.Count == 0)
            return (null, $"nothing to snapshot - no {DatPattern} in {source}");

        Directory.CreateDirectory(SnapshotDir(store));

        var snapshot = new Snapshot
        {
            Id = NextId(store),
            TakenUtc = DateTime.UtcNow,
            Source = source,
            Label = label,
        };

        long stored = 0, deduped = 0;

        foreach (var path in dats)
        {
            var info = new FileInfo(path);
            string hash;

            try
            {
                hash = Hash(path);
            }
            catch (Exception ex)
            {
                // A dat held open by a running client or the server itself is the
                // normal case, not an error - but a snapshot missing a file is a lie,
                // so the whole thing is abandoned rather than silently partial.
                return (null, $"could not read {info.Name}: {ex.Message}");
            }

            snapshot.Files.Add(new FileRecord
            {
                Name = info.Name,
                Size = info.Length,
                Sha256 = hash,
                ModifiedUtc = info.LastWriteTimeUtc,
            });

            var blob = BlobPath(store, hash);

            if (File.Exists(blob))
            {
                deduped += info.Length;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(blob)!);

            // Write beside and move, so an interrupted copy cannot leave a blob that
            // is named after a hash it does not have.
            var tmp = blob + ".partial";
            File.Copy(path, tmp, overwrite: true);
            File.Move(tmp, blob, overwrite: true);
            stored += info.Length;
        }

        File.WriteAllText(Path.Combine(SnapshotDir(store), snapshot.Id + ".json"),
                          JsonSerializer.Serialize(snapshot, Json));

        var pruned = Prune(store, keep, log);

        var message = $"snapshot {snapshot.Id}: {snapshot.Files.Count} file(s), " +
                      $"{Mb(stored)} MB new, {Mb(deduped)} MB already stored" +
                      (pruned > 0 ? $", {pruned} old snapshot(s) pruned" : "");

        log(message);
        return (snapshot, message);
    }

    /// <summary>
    /// A timestamp to the second, made unique by suffix if it has to be.
    ///
    /// The id is what an admin types at 2am, so it stays human - a sortable
    /// timestamp, not a guid. But a manual snapshot taken in the same second as the
    /// startup one would otherwise land on the same manifest filename and silently
    /// replace it, which is a backup quietly disappearing. Found by the tests.
    /// </summary>
    private static string NextId(string store)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var id = stamp;

        for (var n = 2; File.Exists(Path.Combine(SnapshotDir(store), id + ".json")); n++)
            id = $"{stamp}-{n}";

        return id;
    }

    private static string Hash(string path)
    {
        // Share everything: this reads files a client or the server may have open,
        // and reading is all it does.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete, 1 << 20);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long Mb(long bytes) => bytes / 1024 / 1024;

    // ------------------------------------------------------------------ reading

    public static List<Snapshot> List(string store)
    {
        var dir = SnapshotDir(store);

        if (!Directory.Exists(dir))
            return new List<Snapshot>();

        var found = new List<Snapshot>();

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(file), Json);
                if (s is not null) found.Add(s);
            }
            catch { /* a manifest we cannot read is skipped, not fatal */ }
        }

        return found.OrderByDescending(s => s.TakenUtc).ToList();
    }

    public static Snapshot? Find(string store, string id) =>
        List(store).FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Newest first, so "the last known good one" is Latest().</summary>
    public static Snapshot? Latest(string store) => List(store).FirstOrDefault();

    // ---------------------------------------------------------------- verifying

    /// <summary>
    /// Confirms every blob a snapshot names is present and still hashes to its name.
    /// A backup nobody has verified is a hope, not a backup.
    /// </summary>
    public static (bool Ok, List<string> Problems) Verify(string store, Snapshot snapshot)
    {
        var problems = new List<string>();

        foreach (var f in snapshot.Files)
        {
            var blob = BlobPath(store, f.Sha256);

            if (!File.Exists(blob)) { problems.Add($"{f.Name}: blob {f.Sha256[..12]} is missing"); continue; }
            if (new FileInfo(blob).Length != f.Size) { problems.Add($"{f.Name}: blob is the wrong size"); continue; }

            try
            {
                if (!Hash(blob).Equals(f.Sha256, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{f.Name}: blob does not match its hash");
            }
            catch (Exception ex) { problems.Add($"{f.Name}: {ex.Message}"); }
        }

        return (problems.Count == 0, problems);
    }

    // ---------------------------------------------------------------- restoring

    /// <summary>
    /// Copies a snapshot's files back over <paramref name="destination"/>.
    ///
    /// Checked before anything is written: every blob must be present, and every
    /// target must be writable. A dat held open by the server or a running client
    /// cannot be replaced, and finding that out half way through would leave a set
    /// that is neither the old one nor the new one.
    /// </summary>
    public static (bool Ok, string Message) Restore(
        string store, Snapshot snapshot, string destination, Action<string>? log = null)
    {
        log ??= _ => { };

        if (!Directory.Exists(destination))
            return (false, $"{destination} does not exist");

        foreach (var f in snapshot.Files)
            if (!File.Exists(BlobPath(store, f.Sha256)))
                return (false, $"cannot restore - the stored copy of {f.Name} is missing");

        var locked = new List<string>();

        foreach (var f in snapshot.Files)
        {
            var target = Path.Combine(destination, f.Name);

            if (!File.Exists(target))
                continue;

            try
            {
                using var probe = new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None);
            }
            catch { locked.Add(f.Name); }
        }

        if (locked.Count > 0)
            return (false, $"cannot restore - in use by a running client or the server: {string.Join(", ", locked)}. " +
                           "Stop them and try again.");

        var restored = 0;

        foreach (var f in snapshot.Files)
        {
            var target = Path.Combine(destination, f.Name);
            File.Copy(BlobPath(store, f.Sha256), target, overwrite: true);
            File.SetLastWriteTimeUtc(target, f.ModifiedUtc);
            restored++;
            log($"restored {f.Name}");
        }

        return (true, $"restored {restored} file(s) from snapshot {snapshot.Id} into {destination}");
    }

    // ------------------------------------------------------------------ pruning

    /// <summary>
    /// Keeps the newest <paramref name="keep"/> snapshots and deletes any blob no
    /// surviving snapshot refers to. Returns how many snapshots went.
    /// </summary>
    public static int Prune(string store, int keep, Action<string>? log = null)
    {
        log ??= _ => { };

        if (keep <= 0)
            return 0;

        var all = List(store);

        if (all.Count <= keep)
            return 0;

        var doomed = all.Skip(keep).ToList();

        foreach (var s in doomed)
        {
            try { File.Delete(Path.Combine(SnapshotDir(store), s.Id + ".json")); }
            catch (Exception ex) { log($"could not remove snapshot {s.Id}: {ex.Message}"); }
        }

        // Only now is it safe to work out what is unreferenced.
        var live = List(store).SelectMany(s => s.Files).Select(f => f.Sha256)
                              .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(BlobDir(store)))
        {
            foreach (var blob in Directory.GetFiles(BlobDir(store), "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(blob);

                if (name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) || !live.Contains(name))
                {
                    try { File.Delete(blob); }
                    catch (Exception ex) { log($"could not remove blob {name[..Math.Min(12, name.Length)]}: {ex.Message}"); }
                }
            }
        }

        return doomed.Count;
    }

    /// <summary>Bytes the blob store is actually using, for the status command.</summary>
    public static long StoreSize(string store)
    {
        if (!Directory.Exists(BlobDir(store)))
            return 0;

        return Directory.GetFiles(BlobDir(store), "*", SearchOption.AllDirectories)
                        .Sum(f => new FileInfo(f).Length);
    }
}
