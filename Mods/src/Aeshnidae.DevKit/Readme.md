# Aeshnidae.DevKit

The content developer's toolkit. In-game commands that cover the whole loop of making
and changing world content - look at a weenie, get its SQL, put an edited copy back,
see exactly what changed, and hand the change to whoever applies it - for developers
who have no way onto the server and are not supposed to need one.

Supersedes `Aeshnidae.ContentTools` (its `/wexport` lives here now).

The same dll runs on **live** and on **staging** (see `ops/staging.sh`); `Settings.json`
is what makes them behave differently. On live only the read-only commands do anything.

## Commands (Developer+)

| Command | Where | What |
| --- | --- | --- |
| `/winfo <wcid\|classname> [full]` | any | Describe a weenie in content terms: emotes decoded (`Give <22419 Snow Tusker Leader Tusk>: AwardXP 95,000,000 ; Tell "..."`), what it sells, what it spawns, its spells, then every property with its name. `full` adds palettes, textures and anim parts. |
| `/wfind <word> [word...]` | any | Weenies whose name contains every word. |
| `/wexport <wcid\|classname>` | any | The weenie's SQL, as `/export-sql` writes it, posted to `#content` as a file. |
| `/lbexport [landblock]` | any | A landblock's placed instances as SQL, to `#content`. No argument = where you stand. |
| `/wimport <wcid\|file>` | **staging** | Import `<content_folder>/sql/weenies/<wcid> *.sql`, reload the weenie, refresh its live instances (via `/clearweenie`), then show the diff against live. Refused where `AllowImport` is off. |
| `/wdiff <wcid> [...]` | staging | What this server's weenie changes against live: `~ changed  - removed  + added`, in `/winfo`'s words. |
| `/lbdiff [landblock]` | staging | Same for a landblock's instances: placed, removed, moved, rotated, relinked. |
| `/submit <slug> <wcid\|lb:XXXX\|lb:here ...> [-- why]` | staging | Builds `YYYY-MM-DD_NNN_<slug>.sql` (the ledger's `_template.sql` shape, full-weenie replacements with the diff in the header) and `.patchnote.md` (skeleton with the values filled in), saves both under the content folder's `submitted/`, posts both to `#content`. |
| `/devkit` | any | Status: which server this is, what is allowed, where things go. |
| `/devkit-reload` (Admin) | any | Re-read `Settings.json`. |

Every command works from the console too (`ace-cmd.sh --staging 'winfo 22642'`), so
the loop can be exercised without a client.

## How the diff works

Staging holds two copies of the world: `aeshnidae_staging_world`, which ACE runs and
developers change, and `aeshnidae_staging_base`, the untouched clone taken at the same
moment. Diff reads both over a raw connection (ACE's data layer only knows one schema)
and compares rows **by content, never by id** - an export/edit/import re-inserts every
row under a weenie with new autoincrement ids, and an id-based diff would call all of
them changed. Emotes are folded together with their actions before comparing, because
the actions hang off the emote's id.

## Settings.json

```json
{
  "Enabled": true,
  "AllowImport": false,            // TRUE ON STAGING ONLY. Live changes go through the ledger.
  "BaseSchema": "",                // "aeshnidae_staging_base" on staging; empty on live
  "SubmitDirectory": "",           // default <content_folder>/submitted
  "ServerLabel": "",               // "staging" / "live"; default the WorldName
  "WebhookUrl": "https://discord.com/api/webhooks/…",   // #content
  "Username": "Aeshnidae DevKit",
  "MaxAttachmentBytes": 8388608,
  "MaxFindResults": 40,
  "MaxDiffLines": 80
}
```

The webhook is a credential; the file is gitignored and `/devkit` prints it masked.
`staging.sh setup`/`refresh --mods` writes the staging values automatically.

## What it deliberately does not do

- **Write to live.** `/wimport` checks `AllowImport`, which is false on live. Everything
  reaching live goes through `Mods/Content/sql/changes/` and `push-content.sh`, which
  snapshots, applies, refreshes and records. `/submit` produces exactly what that wants.
- **Assign the ledger's sequence number.** Submissions are named `..._NNN_...`; the
  person applying renames `NNN` to the day's next number, because only they know it.
- **Depend on other mods' types.** `/wimport` reaches `/clearweenie` by dispatching the
  command by name; if `Aeshnidae.ClearWeenie` is not loaded it says so and the eviction
  alone means the next spawn is right.
