# Aeshnidae.RemoteConsole

A console for a server that has none.

Aeshnidae runs under systemd with `ACE_NONINTERACTIVE_CONSOLE=true`, so ACE never starts
its command prompt. Until this mod, the only way to run a console command was to log in
as a Developer and type it in chat - and every content push ended with "now run this in
game". This closes that gap so the whole pipeline can be finished by a script.

## Use it

From the PC:

```
bash C:\Aeshnidae\ops\ace-cmd.sh 'clearweenie 22642 3930'
bash C:\Aeshnidae\ops\ace-cmd.sh 'mod list' 'mod enable Aeshnidae.Foo'
printf 'mod list\nclearweenie 9495\n' | bash C:\Aeshnidae\ops\ace-cmd.sh -
```

Or by hand on the VPS: write `<name>.cmd` into
`~/ace/Mods/Aeshnidae.RemoteConsole/inbox/`, one command per line, `#` for comments.
A `<name>.done` receipt appears when it has run.

## What it does

Watches the inbox once a second. For each `.cmd` file, in name order:

1. Renames it to `.running` - atomic, so nothing runs a file twice.
2. Runs each line exactly as `CommandManager.CommandThread` would from the keyboard:
   `ParseCommand`, `GetCommandHandler` with a **null session**, `Invoke`. Nothing is
   reimplemented; the same handlers, the same access rules.
3. Writes `<name>.done` with a per-line verdict and the start/finish timestamps.

The receipt's verdicts:

| | |
| --- | --- |
| `ok` | handler ran without throwing |
| `no such command` | |
| `needs a player in the world` | `CommandHandlerFlag.RequiresWorld` - refused, as on a real console |
| `threw X: ...` | the handler threw; full trace is in the log |

## Output

Console output goes where it always went: the log, which systemd captures in the
journal. The receipt carries `started` / `finished` so a caller can pull that slice -
`ace-cmd.sh` does this and prints it after the receipt. Capturing output in-process was
rejected: it would need a log4net appender to reproduce what `journalctl` already does.

## Security

The inbox directory **is** the permission boundary. Anyone who can write there can run
any console command as the server. It lives under the mod folder, owned by the account
the server runs as, and is reachable only by someone who already has ssh to the box -
the boundary that already protects everything else. Do not point `InboxDirectory` at
anything world-writable.

## Settings

| | Default | |
| --- | --- | --- |
| `InboxDirectory` | `inbox` | relative to the mod folder, or absolute |
| `PollSeconds` | `1` | |
| `ReceiptLifetimeMinutes` | `60` | `.done` files older than this are swept; `0` keeps them |
