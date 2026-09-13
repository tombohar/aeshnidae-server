# Aeshnidae.FairPlay

Reference mod. Copy the folder, rename it, and you have a working starting point.

What it demonstrates:

| File | Shows |
| --- | --- |
| `Mod.cs` | The `IHarmonyMod` entry point ACE resolves by convention, and correct patch teardown in `Dispose`. |
| `PatchClass.cs` | A Harmony postfix on `Player.PlayerEnterWorld`, plus a dormant categorized patch. |
| `Commands.cs` | `[CommandHandler]` methods auto-registered and unregistered by `ModContainer`. |
| `Settings.cs` | `Settings.json` written next to the dll on first run. |
| `Meta.json` | What `ModManager` reads to decide whether to load this at all. |

See `..\README.md` for the build and rename rules.
