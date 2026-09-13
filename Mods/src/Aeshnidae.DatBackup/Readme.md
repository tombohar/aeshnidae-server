# Aeshnidae.DatBackup

Snapshots the server's dat files on every start, so there is always a known-good set
to go back to.

## Why

`Aeshnidae.Instances` publishes cloned geometry to clients over DDD, and part of that
is **replacing the stock terrain record `0xLLLLFFFF`** for each instance landblock
(see `DatPublisher`). The server's own dats on disk are never written — the
replacement is in-memory, in the DDD registrations — but the client writes what it
downloads into its own cell dat.

That is how three landblocks went missing. On 2026-09-07 the Aeshnidae dat profile was
found to be short `0x0300FFFF`, `0x0301FFFF` and `0x0302FFFF` against a pristine set,
after instances were created and then deleted. Nothing was structurally corrupt — every
block chain in all four dats was intact — the records were simply gone.

So there are two sets worth protecting, and they are protected in two places:

| set | protected by | when |
| --- | --- | --- |
| the server's `DatFilesDirectory` | this mod | server start |
| each client dat profile | AeshLauncher | launcher start |

Both use the same `SnapshotStore`, which is why that file is plain BCL with no ACE
types in it: the launcher links the same source file.

## How it stores things

Content-addressed. Each distinct file is stored once under its SHA-256 in `blobs/`,
and a snapshot is a small manifest in `snapshots/` naming the hashes it wants.

The shape follows from what dats are: four files, 1.4 GB together, that change rarely
and never partially. Copying all of them per snapshot would spend a gigabyte recording
that nothing happened. Measured on the real set:

```
first snapshot   4 file(s), 1344 MB new,    0 MB already stored
second snapshot  4 file(s),    0 MB new, 1344 MB already stored
```

The second snapshot cost a 1 KB manifest. That is what makes it reasonable to take one
on every start, and why `KeepSnapshots` can be generous.

## Settings

`Settings.json`, written with defaults on first run, next to the dll.

| | |
| --- | --- |
| `SnapshotOnStartup` | Take a snapshot when the server starts. Default true. |
| `BackupDirectory` | Where snapshots live. Empty = `DatBackup` beside this mod. |
| `DatDirectory` | What to snapshot. Empty = the server's `DatFilesDirectory` from Config.js. |
| `KeepSnapshots` | Generations to keep. Default 20. |
| `VerifyOnStartup` | Re-hash the newest snapshot at startup. Default false — it reads the whole store. |

## Commands

Admin only.

```
/datbackup                 take a snapshot now
/datbackup list            what is stored, and how much disk it uses
/datbackup verify [id]     re-hash a snapshot's stored copies (newest if no id)
/datbackup restore <id>    put a snapshot back where it came from
/datbackup-reload          restart the mod, re-reading Settings.json
```

Restore is checked before it writes anything: every blob must be present, and every
target must be writable. A dat held open by the server or a running client cannot be
replaced, and discovering that half way through would leave a set that is neither the
old one nor the new one — so it refuses and names what is holding them. Restart the
server afterwards so it re-reads the dats.

## Notes

- The startup snapshot runs on a background thread. Hashing 1.4 GB takes ~15 seconds,
  and nothing is waiting on it. A failure is logged and dropped: a server that will not
  start because a backup failed is a worse outcome than a missing snapshot.
- Snapshot ids are a timestamp to the second, suffixed `-2`, `-3` if two land in the
  same second. Without that a manual snapshot taken in the same second as the startup
  one silently replaced it — found by the tests, not in production.
- Verify exists because a backup nobody has checked is a hope. It re-reads every blob
  and compares it to the hash it is filed under.
