# Aeshnidae server source

Aeshnidae is an Asheron's Call server built on [ACEmulator/ACE](https://github.com/ACEmulator/ACE).
ACE is licensed under the [GNU Affero General Public License v3](LICENSE). Because the
server runs a modified ACE and players interact with it over the network, the AGPL (§13)
entitles every player to the source of exactly what is running. This repository is that
source, and nothing else.

Last synced from the live server: **2026-09-14**.

## What is here

| | |
| --- | --- |
| `Source/`, `Database/`, everything except `Mods/` | ACEmulator/ACE at commit [`1723b966`](https://github.com/ACEmulator/ACE/tree/1723b966c27df6765373367211fae01e88834af3), **unmodified**. Diff this tree against that commit and only `Mods/` and this README differ. |
| `Mods/src/*` | The Aeshnidae mods: separate assemblies loaded by ACE's built-in mod host, which [Harmony](https://github.com/pardeike/Harmony)-patch the server at runtime. Every mod listed below is loaded on the live server; a mod that is switched off is removed from here on the next sync. |
| `Mods/Directory.Build.props` | Shared build settings for the mods. |

All code under `Mods/` is licensed under the AGPL-3.0, the same as ACE.

Not here, and not required by the licence: the game world's database content, server
configuration and credentials, and the tooling used to run the server.

## Building

1. Build ACE as upstream documents (`Source/ACE.sln`, Release, x64, .NET 10).
2. Build any mod against it: `dotnet build Mods/src/<Mod>/<Mod>.csproj -c Release`.
   Output lands in `Mods/<Mod>/`, which is the layout ACE's `ModManager` loads from
   (`Mods/<Name>/<Name>.dll` beside its `Meta.json`).
3. Each mod reads a `Settings.json` beside its dll if one exists; the defaults are in
   its `Settings.cs`.

## Mods on the live server

| Mod | What it does |
| --- | --- |
| [`Aeshnidae.AdminAudit`](Mods/src/Aeshnidae.AdminAudit/) | Tamper-evident audit trail of privileged actions: commands, item creation, grants, bank and instance activity. |
| [`Aeshnidae.Bank`](Mods/src/Aeshnidae.Bank/) | Per-account bank for pyreals, luminance, legendary keys and sturdy iron keys. /bank or /b. |
| [`Aeshnidae.ClearWeenie`](Mods/src/Aeshnidae.ClearWeenie/) | Evicts one weenie from the cache and refreshes its live instances, without the global /clearcache spike |
| [`Aeshnidae.DatBackup`](Mods/src/Aeshnidae.DatBackup/) | Content-addressed snapshots of the server's dat files, taken on startup, with restore. |
| [`Aeshnidae.DevKit`](Mods/src/Aeshnidae.DevKit/) | Content developer toolkit: inspect, import, diff and submit weenies and landblock placements |
| [`Aeshnidae.DiscordRelay`](Mods/src/Aeshnidae.DiscordRelay/) | Relays in-game Trade and General chat to Discord webhooks. |
| [`Aeshnidae.Enlightenment`](Mods/src/Aeshnidae.Enlightenment/) | NPC-driven enlightenment with configurable requirements, keeps and perks. |
| [`Aeshnidae.FairPlay`](Mods/src/Aeshnidae.FairPlay/) | One human, one presence: character limits, Marketplace mule rule, same-IP allegiance block, and login IP correlation. |
| [`Aeshnidae.Fellowship`](Mods/src/Aeshnidae.Fellowship/) | Fellowship size and experience sharing, as settings rather than constants. /fellow |
| [`Aeshnidae.InstancesNoDat`](Mods/src/Aeshnidae.InstancesNoDat/) | Dungeon instances that reuse the source landblock id, so no client dat ever changes. /inst |
| [`Aeshnidae.MaxLevel`](Mods/src/Aeshnidae.MaxLevel/) | Raises the character level cap past retail 275, extrapolating the retail XP curve. Tune in Settings.json. |
| [`Aeshnidae.MoveGuard`](Mods/src/Aeshnidae.MoveGuard/) | Server-side movement validation: rejects client positions that outrun the player's real speed, and reports positions physics could not reach. Defeats Blink-style teleport plugins. |
| [`Aeshnidae.PortalAccess`](Mods/src/Aeshnidae.PortalAccess/) | Removes level requirements from all portals. |
| [`Aeshnidae.QuestBonus`](Mods/src/Aeshnidae.QuestBonus/) | Solved quests grant a small permanent XP multiplier. Tune in Settings.json. |
| [`Aeshnidae.RemoteConsole`](Mods/src/Aeshnidae.RemoteConsole/) | Runs console commands dropped as files into its inbox, so the server can be driven over ssh without an interactive console |
| [`Aeshnidae.SkillMastery`](Mods/src/Aeshnidae.SkillMastery/) | Post-retail skill progression that survives enlightenment. /mastery |
