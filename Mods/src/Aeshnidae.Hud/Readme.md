# Aeshnidae.Hud

The HUD feed: panels the server describes, drawn as windows by a client that can.

The stock client cannot show a window it was not compiled with. OpenAC can - a plugin
registers a window from markup and the host draws it in the game's own look, from the
retail dats. So the server sends each panel as one line of JSON on a chat type no
window shows (0x21), the `AeshHud` plugin (`Mods\Content\tools\AeshHud`) turns it
into a window, and every button on it is a server command sent back as if typed.
Nothing about a panel is known to the client in advance: a new panel on the server is
a new window, no client change.

```
/hud on          start the feed (the plugin sends this itself after login)
/hud off         stop it
/hud sync        re-send every panel
```

## Who does what

This mod owns the **switch** and the **fan-out**. The panels live with their numbers:

| Panel | Provider | Command |
| --- | --- | --- |
| Skills | `Aeshnidae.SkillMastery` | `/hud-skills` |
| Bank | `Aeshnidae.Bank` | `/hud-bank` |

`/hud on` marks the session as listening and then invokes every registered command
whose name starts with `hud-`, found in ACE's command registry at call time. A
provider registers `hud-<panel>` (Player, RequiresWorld), sends its panel from it if
the session is listening, and re-sends whenever its numbers change. To add a panel,
add a `hud-x` command to whichever mod owns the numbers; nothing here changes.

## The contract

`Feed.cs` - the chat type, the listening flag, `IsOn`, `Send`, and the wire shape in
its header comment. A mod cannot use another mod's types (each loads in its own
context), so every provider carries a **verbatim copy** as `HudFeed.cs` in its own
namespace. Keep them identical; the file is written to be copied.

The listening flag is an ephemeral `PropertyBool` (9501) on the Player: in memory only,
never in the biota or the shard database, gone at logout - so a stock client logging in
tomorrow cannot inherit it. Every mod that touches the flag calls
`Feed.RegisterProperty()` from `Initialize`, because the flag has to be in
`EphemeralProperties.PropertiesBool` before the first `SetProperty` or that write
would land in the biota.

## Wire shape

```
{ "v":1, "id":"bank", "title":"Aeshnidae - Bank", "sub":"...",
  "cols":[{"n":"Currency","w":130}, {"n":"Banked","w":120}, {"n":"Carried","w":0}],
  "rows":[{"k":"p","c":["Pyreals","1,234,567","20,000"],"col":"#9BE39B"}, ...],
  "flds":[{"k":"amount","l":"Amount","w":80}, {"k":"to","l":"Pay to","w":130}],
  "acts":[{"l":"Deposit","c":"/b d {key} {amount}","row":true},
          {"l":"Pay","c":"/b pay {to} {key} {amount}","row":true},
          {"l":"Refresh","c":"/hud-bank"}] }
```

Cells are strings, already formatted. `w: 0` on the last column means "the rest".
`col` is a row colour. A field is a text box; an action is a button whose command is
sent as typed, `{key}` replaced by the selected row's key (the action is disabled
without a selection when `row` is true) and `{<field>}` by what was typed there - a
blank field goes in blank, so `/b d p` with no amount means all of it, as it does
typed. Up to 8 columns, 4 fields, 6 actions.

No settings, no patches.
