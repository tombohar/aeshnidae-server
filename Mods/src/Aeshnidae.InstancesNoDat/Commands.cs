namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Admin-only, and deliberately raw. This exists to prove the routing works before
/// anything is built on top of it - portals, pools and access control belong in a
/// later slice, once two players in two copies of one dungeon has actually held up.
/// </summary>
public static class Commands
{
    [CommandHandler("inst", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, -1,
        "Dungeon instances that need no client dat changes. Experimental.",
        "/inst                 what is open, and where you are\n" +
        "/inst enter <copy>    move yourself into a copy of the dungeon you are standing in\n" +
        "/inst leave           back to the original landblock\n" +
        "/inst master          stand in the master copy to author it, and stay there\n" +
        "/inst reload          push the master's content into every open copy\n" +
        "/inst goto <player>   join whichever copy a player is actually in\n" +
        "/inst close           drop every copy")]
    public static void HandleNoInst(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null)
            return;

        if (!Mod.Settings.Enabled)
        {
            Reply(session, "InstancesNoDat is disabled in Settings.json. It is experimental - " +
                           "turn it on deliberately, on a server you are willing to restart.");
            return;
        }

        var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";

        switch (verb)
        {
            case "enter": Enter(session, parameters); break;
            case "leave": Leave(session); break;
            case "resync": Resync(session); break;
            case "count": Count(session, parameters); break;
            case "help": Help(session); break;
            case "master": Master(session); break;
            case "reload": Reload(session); break;
            case "goto": Goto(session, parameters); break;
            case "close": Close(session); break;
            case "instance": Instance(session, parameters); break;
            default: Status(session); break;
        }
    }

    /// <summary>
    /// Every command this mod adds, admin and player, in one place. Worth a command
    /// rather than a document because the question is always asked in game.
    /// </summary>
    private static void Help(Session session)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Instanced dungeons - private copies that need no client dat changes.");
        sb.AppendLine("");
        sb.AppendLine("PLAYERS");
        sb.AppendLine("  /instance                 which copy of a dungeon you are in,");
        sb.AppendLine("                            and its enlightenment difficulty tier");
        sb.AppendLine("");
        sb.AppendLine("ADMIN - flagging a dungeon");
        sb.AppendLine("  /inst instance          list what is flagged, and how many copies are open");
        sb.AppendLine("  /inst instance 002B personal      a copy each");
        sb.AppendLine("  /inst instance 002B fellowship    a copy per party");
        sb.AppendLine("  /inst instance 002B allegiance    a copy per allegiance");
        sb.AppendLine("  /inst instance 002B               unflag it");
        sb.AppendLine("");
        sb.AppendLine("ADMIN - authoring, in this order");
        sb.AppendLine("  /inst master            go to the master FIRST - the only version that is authored");
        sb.AppendLine("  /createinst <wcid|name>   place a permanent object at your feet");
        sb.AppendLine("  /removeinst               remove the object you last appraised");
        sb.AppendLine("  /inst reload            push the master into every open copy");
        sb.AppendLine("");
        sb.AppendLine("  Author in the master, never in a copy. /createinst spawns into whichever");
        sb.AppendLine("  landblock you are standing in, so doing it inside a copy puts the object");
        sb.AppendLine("  somewhere only that copy can see until the copy is closed.");
        sb.AppendLine("");
        sb.AppendLine("ADMIN - moving around and looking");
        sb.AppendLine("  /inst                   where you are, and every open copy");
        sb.AppendLine("  /inst count <name>      how many of a thing each copy really holds");
        sb.AppendLine("  /inst resync            clear a phantom player somebody can still see");
        sb.AppendLine("  /inst goto <player>     join whichever copy a player is really in");
        sb.AppendLine("  /teleto <player>          the same - patched to follow into copies");
        sb.AppendLine("  /inst enter <n>         put yourself in copy n of where you stand");
        sb.AppendLine("  /inst leave             back to the master");
        sb.AppendLine("  /inst close             drop every copy; anyone inside is recalled");
        sb.AppendLine("  /inst help              this");

        Reply(session, sb.ToString().TrimEnd());
    }

    private static void Status(Session session)
    {
        var player = session.Player!;
        var here = (ushort)(player.Location.LandblockId.Raw >> 16);
        var sb = new StringBuilder();

        var orphaned = InstanceWorld.IsOrphaned(player.CurrentLandblock)
            ? "  <- CLOSED COPY, nothing ticks this" : "";

        sb.AppendLine($"Standing in {here:X4}, copy {InstanceWorld.CopyOf(player.CurrentLandblock)} " +
                      $"(0 is the server's own landblock).{orphaned}");
        sb.AppendLine($"Open copies: {InstanceWorld.Count}. Players assigned: {Context.AssignedCount}.");

        if (Mod.Settings.ScaleWithEnlightenment)
        {
            var cap = Mod.Settings.MaxEnlightenmentsCounted;

            sb.AppendLine($"Enlightenment scaling (personal copies only): " +
                          $"+{Mod.Settings.DifficultyPercentPerEnlightenment:0.##}% difficulty, " +
                          $"+{Mod.Settings.RewardPercentPerEnlightenment:0.##}% reward, per enlightenment" +
                          $"{(cap > 0 ? $", counted to {cap}" : "")}. You are EL {player.Enlightenment}.");
        }
        else
            sb.AppendLine("Enlightenment scaling: off. Copies are stock difficulty.");

        foreach (var (key, block) in InstanceWorld.All.OrderBy(a => a.Key.Landblock).ThenBy(a => a.Key.Copy))
        {
            var people = InstanceWorld.PlayersIn(block);
            var names = people.Count > 0 ? " - " + string.Join(", ", people.Select(pl => pl.Name)) : "";

            // The enlightenment the copy was OPENED at, which is what its monsters were
            // built from - not the owner's enlightenment now. Those differ the moment
            // somebody enlightens without closing their dungeon, and when a player says
            // "the scaling did not apply" this is the line that answers it.
            var counted = Scaling.Counted(key.Landblock, key.Copy);

            var scaled = Mod.Settings.ScaleWithEnlightenment && counted > 0
                ? $", tier {counted} " +
                  $"(+{counted * Mod.Settings.DifficultyPercentPerEnlightenment:0.##}% harder, " +
                  $"+{counted * Mod.Settings.RewardPercentPerEnlightenment:0.##}% richer)"
                : "";

            sb.AppendLine($"  {key.Landblock:X4} copy {key.Copy}: {people.Count} player(s), " +
                          $"{InstanceWorld.ObjectCount(block)} object(s){names}{scaled}");
        }

        Reply(session, sb.ToString().TrimEnd());
    }

    /// <summary>
    /// How many objects matching a name each copy holds, and the master alongside them.
    ///
    /// The question that keeps coming up is "is this thing really in that copy, or is the
    /// client just showing it to me", and until now the only way to guess was to read
    /// teardown counts out of the log. This answers it directly.
    /// </summary>
    /// <summary>
    /// Re-tells every client in this dungeon who is and is not actually beside them.
    ///
    /// For the one failure that leaves no trace on the server: a client still drawing
    /// somebody who has moved to another copy. Server-side tracking is correct, so no
    /// audit can find it - the missing message just has to be sent again.
    /// </summary>
    private static void Resync(Session session)
    {
        var here = (ushort)(session.Player!.Location.LandblockId.Raw >> 16);

        InstanceWorld.ResyncVisibility(here);

        Reply(session, $"Told every client in {here:X4} who is really beside them. " +
                       "A phantom player should disappear within a moment.");
    }

    private static void Count(Session session, string[] parameters)
    {
        var player = session.Player!;

        if (parameters.Length < 2)
        {
            Reply(session, "usage: /inst count <part of a name>, e.g. /inst count drudge");
            return;
        }

        var text = string.Join(" ", parameters.Skip(1));
        var here = (ushort)(player.Location.LandblockId.Raw >> 16);
        var sb = new StringBuilder();

        sb.AppendLine($"Objects matching \"{text}\" in {here:X4}:");

        var master = LandblockManager.GetLandblock(new LandblockId((uint)here << 16 | 0xFFFF), false);

        sb.AppendLine($"  you: {InstanceWorld.Describe(player)}");
        sb.AppendLine($"  master: {InstanceWorld.CountMatching(master, text)} " +
                      $"(of {InstanceWorld.ObjectCount(master)} objects)");

        foreach (var line in InstanceWorld.DescribeMatching(master, text))
            sb.AppendLine($"    {line}");

        foreach (var (key, block) in InstanceWorld.All.Where(a => a.Key.Landblock == here)
                                                     .OrderBy(a => a.Key.Copy))
        {
            sb.AppendLine($"  copy {key.Copy}: {InstanceWorld.CountMatching(block, text)} " +
                          $"(of {InstanceWorld.ObjectCount(block)} objects)");

            foreach (var line in InstanceWorld.DescribeMatching(block, text))
                sb.AppendLine($"    {line}");
        }

        Reply(session, sb.ToString().TrimEnd());
    }

    private static void Enter(Session session, string[] parameters)
    {
        var player = session.Player!;

        if (parameters.Length < 2 || !int.TryParse(parameters[1], out var copy) || copy < 1)
        {
            Reply(session, "usage: /inst enter <copy>, where copy is 1 or higher.");
            return;
        }

        if (copy > Mod.Settings.MaxCopiesPerLandblock)
        {
            Reply(session, $"Copy {copy} is above the configured maximum of {Mod.Settings.MaxCopiesPerLandblock}.");
            return;
        }

        var here = (ushort)(player.Location.LandblockId.Raw >> 16);

        if (Mod.Settings.InteriorsOnly && !HasInterior(here))
        {
            Reply(session, $"{here:X4} has no dungeon interior. Copies are restricted to interiors - " +
                           "a surface landblock would need adjacency and terrain seams reasoned through, " +
                           "and they have not been.");
            return;
        }

        var block = InstanceWorld.GetOrCreate(here, copy);

        if (block is null)
        {
            Reply(session, "Could not open that copy - see the server log.");
            return;
        }

        if (!InstanceWorld.PlaceIn(player, here, copy, "/inst enter"))
        {
            Reply(session, "Could not move you into that copy - see the server log.");
            return;
        }

        Reply(session, $"You are now in copy {copy} of {here:X4}. " +
                       "Anything you meet in here is yours alone.");
    }

    private static void Leave(Session session)
    {
        var player = session.Player!;
        var here = (ushort)(player.Location.LandblockId.Raw >> 16);
        var copy = Context.CopyFor(player, here);

        if (copy == 0)
        {
            Reply(session, "You are not in a copy of this landblock.");
            return;
        }

        InstanceWorld.PlaceIn(player, here, 0, "/inst leave");

        Reply(session, $"Back on the server's own {here:X4}. The copy closes once it is empty.");
    }

    /// <summary>
    /// Puts a dev in the master copy - ACE's own landblock - and keeps them there.
    ///
    /// This is the one place a dungeon can be authored. /createinst writes world
    /// database rows against a landblock id, and every copy spawns from those rows, so
    /// there is exactly one authored version and this is it. Without this command an
    /// admin walking into an instanced dungeon is handed a private copy like anyone
    /// else, and edits made there would look right and reach nobody.
    /// </summary>
    private static void Master(Session session)
    {
        var player = session.Player!;
        var here = (ushort)(player.Location.LandblockId.Raw >> 16);

        if (!Mod.IsInstanced(here) && InstanceWorld.CopyOf(player.CurrentLandblock) == 0)
        {
            Reply(session, $"{here:X4} is not instanced, so where you are standing IS the master. " +
                           "Edit it with /createinst and /removeinst as normal.");
            return;
        }

        // Clearing the assignment is the part that makes it stick: with one in place the
        // next landblock crossing would route you straight back into your own copy.
        InstanceWorld.PlaceIn(player, here, 0, "/inst master");

        Reply(session, $"You are in the master copy of {here:X4}. Edits here with /createinst " +
                       "and /removeinst are what every copy spawns from - push them out with " +
                       "/inst reload. /inst enter 1 gets you a private copy again.");
    }

    /// <summary>
    /// Re-reads the master into every open copy, so a dev's edits land everywhere at
    /// once rather than only in the landblock they happen to be standing in.
    /// </summary>
    private static void Reload(Session session)
    {
        var player = session.Player!;
        var here = (ushort)(player.Location.LandblockId.Raw >> 16);

        var copies = InstanceWorld.ReloadAllCopies(here);

        // The master is asked for by id, never taken from where the caller happens to be
        // standing. CopyOf answers 0 for the master AND for a closed copy, so reading it
        // off the player reloaded whichever block they were holding - which, for someone
        // stranded in a copy that had been dropped, meant emptying and respawning a dead
        // landblock while the real master was left exactly as it was.
        var master = LandblockManager.GetLandblock(new LandblockId((uint)here << 16 | 0xFFFF), false);

        if (master is not null)
        {
            // Delayed a tick and done ACE's own way - this is what /reload-landblock
            // does, and on the real master SaveDB is correct rather than dangerous.
            var chain = new ActionChain();
            chain.AddDelayForOneTick();
            chain.AddAction(player, () =>
            {
                master.DestroyAllNonPlayerObjects();
                master.Init(true);
            });
            chain.EnqueueChain();
        }

        Reply(session, $"Reloaded the master of {here:X4} and {copies} open copy(ies). " +
                       "Everything respawns from the master's world database rows - give it a " +
                       "moment, then /inst count to check.");
    }

    /// <summary>
    /// Joins whichever copy a player is actually in.
    ///
    /// ACE's /teleto teleports to a Position, and a Position cannot tell two copies of a
    /// dungeon apart - so it lands a dev in their own private copy of the same geometry,
    /// alone, which is useless for watching someone. This assigns the dev to the
    /// target's copy first, so the teleport arrives where the player really is.
    /// </summary>
    private static void Goto(Session session, string[] parameters)
    {
        var player = session.Player!;

        if (parameters.Length < 2)
        {
            Reply(session, "usage: /inst goto <player name>");
            return;
        }

        var name = string.Join(" ", parameters.Skip(1));
        var target = PlayerManager.GetOnlinePlayer(name);

        if (target is null)
        {
            Reply(session, $"{name} is not online.");
            return;
        }

        if (target.Location is null)
        {
            Reply(session, $"{target.Name} has no location yet - still logging in.");
            return;
        }

        var landblock = (ushort)(target.Location.LandblockId.Raw >> 16);
        var copy = InstanceWorld.CopyOf(target.CurrentLandblock);

        // The position first, so we land beside them rather than wherever we were
        // standing in our own copy of the same rooms.
        player.Location = new Position(target.Location);

        InstanceWorld.PlaceIn(player, landblock, copy, "/inst goto");

        Reply(session, copy > 0
            ? $"You are in copy {copy} of {landblock:X4} with {target.Name}."
            : $"{target.Name} is not in a copy - this is the master {landblock:X4}.");
    }

    private static void Close(Session session)
    {
        var count = InstanceWorld.Count;

        InstanceWorld.Clear();

        Reply(session, $"Closed {count} copy(ies). Anyone who was inside has been recalled to " +
                       "their lifestone.");
    }

    /// <summary>
    /// Flags a dungeon and says who shares a copy of it. Once flagged, every existing
    /// route in works - a portal, a recall, an admin teleport, a login - because the
    /// hook is on entry rather than on any particular portal. Nothing needs a new weenie
    /// and nothing needs a world database change.
    /// </summary>
    private static void Instance(Session session, string[] parameters)
    {
        if (parameters.Length < 2)
        {
            if (Mod.Instanced.Count == 0)
            {
                Reply(session, "No instanced dungeons yet. /inst instance <hex landblock> " +
                               "[personal|fellowship|allegiance]");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("Instanced dungeons:");

            foreach (var (lb, entry) in Mod.Instanced.OrderBy(e => e.Key))
            {
                var open = InstanceWorld.All.Count(a => a.Key.Landblock == lb);
                sb.AppendLine($"  {lb:X4}  {entry.Scope,-10}  {open} copy(ies) open" +
                              (string.IsNullOrWhiteSpace(entry.Name) ? "" : $"  \"{entry.Name}\""));
            }

            Reply(session, sb.ToString().TrimEnd());
            return;
        }

        var text = parameters[1].Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

        if (!ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var landblock))
        {
            Reply(session, $"'{parameters[1]}' is not a hex landblock id. Try 8602.");
            return;
        }

        var list = Mod.Settings.Instanced ?? new List<InstancedLandblock>();
        var existing = list.FirstOrDefault(e => e.TryParse(out var p) && p == landblock);

        // No scope given and already flagged: unflag it.
        if (parameters.Length < 3 && existing is not null)
        {
            list.Remove(existing);
            Mod.Settings.Instanced = list;
            Mod.Settings.Save(Mod.ModPath);
            Mod.ReloadInstanced();

            Reply(session, $"{landblock:X4} is no longer instanced. Copies already open stay until empty.");
            return;
        }

        var scopeText = parameters.Length > 2 ? parameters[2] : "personal";

        if (!Enum.TryParse<InstanceScope>(scopeText, ignoreCase: true, out var scope))
        {
            Reply(session, $"'{scopeText}' is not a scope. Use personal, fellowship or allegiance.");
            return;
        }

        if (Mod.Settings.InteriorsOnly && !HasInterior(landblock))
        {
            Reply(session, $"{landblock:X4} has no dungeon interior, so it cannot be instanced.");
            return;
        }

        if (existing is not null)
        {
            existing.Scope = scope;
            Reply(session, $"{landblock:X4} is now shared per {scope.ToString().ToLowerInvariant()}. " +
                           "Copies already open keep their current owners until they empty.");
        }
        else
        {
            list.Add(new InstancedLandblock { Landblock = landblock.ToString("X4"), Scope = scope });
            Reply(session, $"{landblock:X4} now hands out a copy per {scope.ToString().ToLowerInvariant()} " +
                           "on entry. Every portal, recall, teleport and login into it works - " +
                           "nothing else to set up.");
        }

        Mod.Settings.Instanced = list;
        Mod.Settings.Save(Mod.ModPath);
        Mod.ReloadInstanced();
    }


    /// <summary>
    /// The player-facing question: am I in my own copy of this place, or the shared one?
    ///
    /// Worth its own command at player access because "we can see each other" and "we
    /// cannot see each other" are both reported as bugs, and neither reporter can
    /// currently tell which landblock they are standing in.
    /// </summary>
    [CommandHandler("instance", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
        "Which copy of a dungeon you are in.")]
    public static void HandleInstance(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null)
            return;

        if (!Mod.Settings.Enabled)
        {
            Reply(session, "Instanced dungeons are switched off on this server.");
            return;
        }

        var here = (ushort)(player.Location.LandblockId.Raw >> 16);
        var copy = InstanceWorld.CopyOf(player.CurrentLandblock);
        var name = Mod.NameFor(here);
        var where = string.IsNullOrWhiteSpace(name) ? $"{here:X4}" : $"{name} ({here:X4})";

        if (copy > 0)
        {
            var scope = Mod.ScopeFor(here);
            var others = InstanceWorld.PlayersIn(player.CurrentLandblock).Count - 1;

            Reply(session, $"You are in copy {copy} of {where}, shared with " +
                           $"{Context.GroupDescription(player, scope)}.");

            Reply(session, others > 0
                ? $"{others} other player(s) are in here with you."
                : "Nobody else is in here with you.");

            foreach (var line in Scaling.Describe(here, copy))
                Reply(session, line);

            return;
        }

        if (InstanceWorld.IsOrphaned(player.CurrentLandblock))
        {
            Reply(session, $"You are in a copy of {where} that has been closed - an empty room " +
                           "nothing is updating. Recall or relog to get out of it.");
            return;
        }

        if (Mod.IsInstanced(here))
        {
            Reply(session, $"You are in the shared version of {where}, not a copy of it. " +
                           "Leaving and coming back should hand you your own.");

            // Said explicitly rather than left out. Somebody who has enlightened and is
            // wondering why the dungeon feels unchanged is most likely standing in the
            // master, and "nothing about difficulty was mentioned" is a worse answer
            // than "tier 0, because this is the shared version".
            Reply(session, "Dungeon Difficulty: Enlightenment Tier 0 - the shared version is never scaled.");
            return;
        }

        Reply(session, $"{where} is not an instanced dungeon - everyone here is in the same place.");
    }

    /// <summary>
    /// A landblock has an interior if the cell dat holds a LandblockInfo for it. That
    /// is the same record the client uses to decide whether to draw a dungeon, so this
    /// agrees with what the player can actually see.
    /// </summary>
    private static bool HasInterior(ushort landblock)
    {
        try
        {
            var id = (uint)landblock << 16 | 0xFFFE;
            return DatManager.CellDat.AllFiles.ContainsKey(id);
        }
        catch
        {
            return false;
        }
    }

    private static void Reply(Session? session, string message)
    {
        if (session?.Player is null)
        {
            ModManager.Log(message);
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
