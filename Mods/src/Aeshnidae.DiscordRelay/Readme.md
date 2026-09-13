# Aeshnidae.DiscordRelay

Relays in-game **Trade** and **General** chat to Discord webhooks. One-way: nothing
comes back from Discord into the game.

```
/discordrelay                 status - which channels are wired up, queue depth, errors
/discordrelay-test <channel>  post a test line to one channel's webhook
/discordrelay-reload          restart the mod, re-reading Settings.json
```

All three are Admin-only, because the status line names the Discord channels this
server pipes chat into.

## Setup

1. In Discord, for each destination channel: **Edit Channel - Integrations - Webhooks -
   New Webhook**, then **Copy Webhook URL**. Two channels, two webhooks.
2. Paste them into `Mods\Aeshnidae.DiscordRelay\Settings.json` (written with defaults
   on first run):

   ```jsonc
   {
     "Enabled": true,
     "Channels": {
       "General": { "Enabled": true, "WebhookUrl": "https://discord.com/api/webhooks/…", "Username": "" },
       "Trade":   { "Enabled": true, "WebhookUrl": "https://discord.com/api/webhooks/…", "Username": "" }
     },
     "BatchSeconds": 2.0,
     "MaxQueuedPerChannel": 200,
     "MaxMessageLength": 400,
     "LineFormat": "**{name}**: {message}",
     "IgnoredPlayers": [],
     "LogRelayed": false
   }
   ```

3. `/discordrelay-reload`, then `/discordrelay-test Trade`.

**The webhook URL is a credential** - anyone holding it can post into that channel
without authenticating. It only ever lives in `Settings.json` in the *deployed* mod
folder, which `Mods\.gitignore` excludes, so it stays out of git. Keep it that way, and
rotate it in Discord if it leaks. `/discordrelay` prints it masked for the same reason.

## Adding channels

`Channels` is keyed by ACE's `ChatType` name, and anything not listed is not relayed.
Adding LFG or Roleplay is a settings edit, not a code change:

```jsonc
"LFG": { "Enabled": true, "WebhookUrl": "https://discord.com/api/webhooks/…" }
```

`Allegiance`, `Society` and `Olthoi` would work the same way - but they are private
channels, so think before bridging them.

## How it hooks in

The patch is a prefix on the private `TurbineChatHandler.LogTurbineChat`. That method
is called once per message on the last line of `TurbineChatReceived`, and **every**
rejection path returns before reaching it: gags, `chat_echo_only`, the account-age,
played-time and level gates, and a channel switched off with `chat_disable_trade`. So
the relay carries exactly the messages players actually saw, exactly once each.

The obvious alternative - patching the `GameMessageTurbineChat` constructor - runs
*before* those gates, so it would push refused messages to Discord, and it fires again
for every acknowledgement packet.

A prefix rather than a postfix because `LogTurbineChat` itself returns early when the
matching `chat_log_*` server property is off. Whether chat goes to the server log is a
separate decision from whether it goes to Discord.

Two things follow from patching a private method by name:

- An upstream rename breaks it. `Mod.Initialize` resolves the method first and logs a
  clear error instead of throwing, so the mod stays loaded but relays nothing.
- In principle the JIT could inline it into `TurbineChatReceived`, which would bypass
  the patch. Measured, its body is 394 bytes of IL - an order of magnitude past the 32
  byte inlining heuristic - and the patch is applied during startup, before any chat
  packet has caused `TurbineChatReceived` to be jitted. If chat ever stops relaying
  after an ACE upgrade with no error in the log, this is the first thing to suspect.

## Not blocking the game

`SubmitChat` runs on the network thread, mid-packet. It formats a string, enqueues it,
and returns; nothing there blocks, and nothing throws into ACE.

A background task drains the queues every `BatchSeconds` and coalesces everything that
accumulated into **one** POST per channel. That is the rate limiter: Discord allows
roughly 5 requests per 2 seconds per webhook, and a POST per chat line would trip it
the moment Trade got busy. Chat lands in Discord a second or two late and grouped,
which reads better anyway.

The rest of the failure handling:

- `MaxQueuedPerChannel` caps each queue, so Discord being unreachable cannot grow an
  unbounded buffer on a live server. Overflow is dropped and counted, not buffered.
- HTTP 429 is retried once, honouring `Retry-After`; the rest stays queued for the next
  tick.
- HTTP 401/403/404 means the webhook is wrong or deleted. That channel is parked until
  the next reload rather than retried every two seconds forever, and the reason shows up
  in `/discordrelay`.
- `Dispose` unpatches first, then gives the pump ten seconds to flush what it has, so a
  clean shutdown does not swallow the last few lines.

## What players can and cannot do to your Discord

- `allowed_mentions.parse` is empty on every post, so `@everyone`, `@here` and role
  mentions render as plain text. Nothing said in-game can ping the server.
- Markdown characters in names and messages are escaped, so nobody can reformat the
  channel or open an unterminated code block.
- Messages are truncated at `MaxMessageLength`, and control characters and newlines are
  stripped, so one message stays one line.
