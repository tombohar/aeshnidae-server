# Aeshnidae.MoveGuard

Server-side movement validation. Every position the client reports is checked
against the player's real top speed and against the physics engine before the
server adopts it. Built to defeat Blink and anything else that works by writing a
position into the client.

```
/moveguard                    status and counters
/moveguard mode log           measure and report, reject nothing   <- default
/moveguard mode enforce       reject and rubber-band
/moveguard check <name>       one player's ceiling, strikes, latest violation
/moveguard me                 the same, for yourself
/moveguard top                everyone online with a strike, worst first
/moveguard reload             re-read Settings.json
```

Admin only. Mode changes are saved to `Settings.json` and posted to the audit channel.

## The hole this closes

A blink plugin does one thing: it writes a new position into the running client.
The client then reports that position to the server the way it reports every
position, about once a second while moving. Whether the plugin works is entirely a
question of whether the server believes it - and stock ACE does, twice over:

1. **The speed check only fires across landblocks.** `Player.UpdatePlayerPosition`
   refuses a move over 50 units *only when it also crosses more than one landblock*
   (`distSq > MaxSpeedSq && blockDist > 1`). A landblock is 192 units. Anywhere
   inside that - the whole of a dungeon, most of a town - any distance at all is
   accepted at face value.

2. **A failed collision is ignored.** The server does run a physics transition from
   the old position to the new one, the same one the client runs. When a wall is in
   the way the transition stops short - and `UpdateObjectInternalServer` logs that at
   *Debug* level and `set_current_pos(RequestPos)` forces the requested position
   regardless.

So a blink of 30 units across a room passes check 1, and a blink of 3 units through
a door passes check 2. This mod adds the two checks that are missing.

## What it checks

**Speed.** Between two accepted positions a player may cover at most

    top speed x SpeedTolerance x min(elapsed, MaxCatchUpSeconds) + SlackUnits

where *top speed* is computed for that player from the two inputs the client itself
uses: the run animation's distance per second (`MotionTable.GetRunSpeed`) and the
run-rate multiplier ACE derives from the Run skill (`MovementSystem.GetRunRate`,
capped at 4.5 from skill 800). Burden and exhaustion are deliberately left out -
they only ever slow the client down, and the server's view of them can lag by a
tick. The result is an upper bound, and `SpeedTolerance` (1.5) covers what the
formula does not: jump-running, slopes, rounding.

`MaxCatchUpSeconds` is the important knob. Position updates bunch up under lag, so
one update may honestly account for a couple of seconds of running; this caps how
much. It is therefore also *the longest blink that gets through*: a player who stands
still and then blinks is allowed exactly that many seconds of running distance,
once. Set it lower to shorten that, at the cost of rubber-banding honest players
during longer lag spikes. The average rate can never exceed running speed either way
- which is the point: a blink that is no faster than running is not worth having.

**Geometry.** For any move over half a unit the mod asks the physics engine the
question ACE asks and then ignores: from where the server has you, can you reach
where the client says you are? Physics slides along obstacles rather than failing,
so a wall shows up as the resolved position stopping well short of the requested
one. A shortfall over `GeometryTolerance` (2 units) is a strike.

## Modes, and the order to use them in

| | Speed | Geometry |
| --- | --- | --- |
| `Off` | - | - |
| `Log` | logged | logged |
| `Enforce` | **rejected** | logged, or rejected if `EnforceGeometry` |

**Start in `Log`** - the default - and read the server log and the audit channel for
a few days. Every strike is logged with the player, the distance, the elapsed time
and the computed ceiling, so you will see exactly what honest play looks like on
your shard before anything is refused. `/moveguard top` shows who is collecting
strikes.

**Then `Enforce`.** Speed violations are refused. Geometry stays log-only because
server physics does disagree with the client in a handful of honest cases - a door
that closed during lag, a jump across a cell boundary, a monster the server thinks
is in the way - and rubber-banding an honest player is worse than logging a cheat.
Once the geometry log has been quiet for honest players, set `EnforceGeometry`.
That is the step that stops short blinks through doors, which is most of what Blink
is used for.

## What a rejection does

The server's `Location` is left exactly where it was, the force-position sequence
is bumped and a position update is sent - the same pair of calls ACE's own z-hack
response makes. A retail client snaps back. A client that ignores the snap-back
gains nothing: the server's position is where every other player sees you, where
monsters hit you, and what every distance check (pickup, vendor, trade, unlock, spell
range) is measured from. Being somewhere the server does not believe is the same as
not being there.

Nothing is said to the player unless `TellPlayer` is on. Silence is deliberate: an
honest player who lagged sees a small hitch, and a cheater gets no feedback to tune
against.

## Strikes

Strikes are kept per online character and dropped at logout; they are for staff
eyes, not a permanent record - the audit channel is the record.

- `AuditAfterStrikes` (3) in `StrikeWindowSeconds` (60): post to the audit channel,
  and again at every further multiple. One rejected position is lag; three in a
  minute is a pattern.
- `KickAfterStrikes` (0 = never): disconnect at this many *rejected* positions in the
  window - logged-only strikes never count. Off by default because Enforce already
  makes cheating useless; this is for when you would rather they were not online.
- Staff at `ExemptAtOrAbove` (Admin) are logged like anyone else but never
  rubber-banded or kicked.

## What is not judged

- The **first position after login** - there is nothing to measure from.
- **`TeleportGraceSeconds`** (3) after any teleport: portal, recall, lifestone,
  death, admin `/teleloc`, the Marketplace bounce. The client keeps sending positions
  from where it was while the new landblock loads, and ACE's own source notes this.
- **Vertical movement.** Distance is measured in the horizontal plane; falling is
  legitimately fast and ACE already has a z-hack check of its own.
- Anything in `Off` mode. The patches stay in place but measure nothing.

## Settings

`Mods\Aeshnidae.MoveGuard\Settings.json`, then `/moveguard reload`.

| | Default | |
| --- | --- | --- |
| `Mode` | `Log` | `Off`, `Log`, `Enforce` |
| `SpeedTolerance` | `1.5` | Multiplier on the computed top speed |
| `SlackUnits` | `3` | Added to every allowance |
| `MaxCatchUpSeconds` | `2.5` | Longest gap one update may account for - and the longest blink that passes |
| `TeleportGraceSeconds` | `3` | Nothing judged this long after a teleport |
| `FallbackRunSpeed` | `4.5` | Animation units/s if the motion table cannot be read |
| `CheckGeometry` | `true` | Ask physics whether the position is reachable |
| `GeometryTolerance` | `2` | Units the resolved position may fall short by |
| `EnforceGeometry` | `false` | Reject geometry violations in Enforce mode |
| `StrikeWindowSeconds` | `60` | |
| `LogPerWindow` | `5` | Server-log lines per player per window; strikes past it are counted and audited, not logged one by one |
| `AuditAfterStrikes` | `3` | Post to the audit channel at this many in the window |
| `KickAfterStrikes` | `0` | Disconnect at this many rejections; 0 never |
| `TellPlayer` | `false` | Say `PlayerMessage` on a rejection |
| `ExemptAtOrAbove` | `Admin` | Logged but never rejected |

## How it hooks in

Three Harmony patches, no ACE source touched:

- Prefix on `Player.UpdatePlayerPosition` - the judgement. Skips ACE's method and
  returns false on a rejection. Any exception inside the guard is logged and the move
  is *allowed*: a bug here must degrade to stock behaviour, not freeze the shard.
- Postfix on the same method - learns which positions ACE actually accepted. The
  method's return value means "changed landblock", not "accepted", so acceptance is
  read off the side effect: on success ACE assigns `Location = newPosition`, the same
  object. A move refused by ACE's own checks therefore does not advance the clock.
- Postfix on `PlayerManager.SwitchPlayerFromOnlineToOffline` - drops the state.

## Honest about the limits

- A blink shorter than the catch-up allowance, from standstill, gets through the
  speed check once. Geometry catches it if it went through something; open-ground
  short hops are the residual, and they are worth less than running.
- A rejection restarts the clock, so a client that ignores the snap-back and keeps
  reporting the refused position is not waited out - it stays refused until it
  reports something within reach of where the server has it.
- Geometry enforcement will produce some false rejections until it is tuned. That is
  why it ships off, and why the log comes first.
- This validates *where* the client says it is. It does nothing about *what* the
  client says it does - attack and cast rates, packet floods, item manipulation are
  separate checks for separate mods.
- No client can be verified. This works precisely because it never tries to: it
  makes the server's position the only one that counts.
