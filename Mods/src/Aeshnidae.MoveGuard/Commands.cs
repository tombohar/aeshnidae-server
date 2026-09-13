namespace Aeshnidae.MoveGuard;

/// <summary>
/// Admin-only. Everything here is about other players' movement, which is not a
/// thing players should be able to read about each other.
/// </summary>
public static class Commands
{
    [CommandHandler("moveguard", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Movement validation: status, mode, per-player detail.",
        "/moveguard [ mode off|log|enforce | check <name> | me | top | reload ]")]
    public static void HandleMoveGuard(Session session, params string[] parameters)
    {
        try
        {
            var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

            switch (verb)
            {
                case "mode" when parameters.Length > 1: SetMode(session, parameters[1]); return;
                case "check" when parameters.Length > 1: Check(session, parameters[1]); return;
                case "me": Check(session, session.Player?.Name ?? ""); return;
                case "top": Top(session); return;
                case "reload": Mod.Reload(); Status(session); return;
                default: Status(session); return;
            }
        }
        catch (Exception ex)
        {
            Reply(session, $"/moveguard failed: {ex.Message}");
            ModManager.Log($"[{Mod.Name}] /moveguard failed: {ex}", ModManager.LogLevel.Error);
        }
    }

    private static void Status(Session? session)
    {
        var s = Mod.Settings;

        var text = new StringBuilder()
            .AppendLine($"{Mod.Name} v{Mod.Container?.Meta.Version ?? "?"} - {Mod.Container?.Status.ToString() ?? "not loaded"}")
            .AppendLine($"Mode: {s.ModeValue}   (exempt at {s.ExemptLevel}+)")
            .AppendLine($"Speed: top speed x{s.SpeedTolerance:F2} over up to {s.MaxCatchUpSeconds:F1}s, plus {s.SlackUnits:F1} units; {s.TeleportGraceSeconds:F0}s grace after a teleport")
            .AppendLine($"Geometry: {(s.CheckGeometry ? $"checked, tolerance {s.GeometryTolerance:F1} units, {(s.EnforceGeometry ? "enforced" : "log only")}" : "off")}")
            .AppendLine($"Strikes: audit at {s.AuditAfterStrikes} in {s.StrikeWindowSeconds}s, kick at {(s.KickAfterStrikes > 0 ? s.KickAfterStrikes.ToString() : "never")}")
            .AppendLine($"Since start - speed {Mod.SpeedViolations}, geometry {Mod.GeometryViolations}, rejected {Mod.Rejections}, kicked {Mod.Kicks}; tracking {Tracker.Count} online")
            .ToString();

        Reply(session, text);
    }

    private static void SetMode(Session? session, string value)
    {
        if (!Enum.TryParse<Mode>(value, true, out var mode))
        {
            Reply(session, "Mode is off, log or enforce.");
            return;
        }

        Mod.Settings.Mode = mode.ToString();
        Mod.Settings.Save(Mod.ModPath);

        var who = session?.Player?.Name ?? "console";
        ModManager.Log($"[{Mod.Name}] mode set to {mode} by {who}");
        PlayerManager.BroadcastToAuditChannel(session?.Player, $"[MoveGuard] {who} set movement validation to {mode}");

        Reply(session, $"Mode is now {mode}.");
    }

    /// <summary>One player: their computed ceiling and everything recorded against them.</summary>
    private static void Check(Session? session, string name)
    {
        var player = PlayerManager.GetOnlinePlayer(name);

        if (player is null)
        {
            Reply(session, $"{name} is not online. (History is kept only while a player is online.)");
            return;
        }

        var runSkill = player.GetCreatureSkill(Skill.Run)?.Current ?? 0;
        var ceiling = Speed.CeilingFor(player);
        var allowance = ceiling * Mod.Settings.SpeedTolerance * Mod.Settings.MaxCatchUpSeconds + Mod.Settings.SlackUnits;

        var text = new StringBuilder()
            .AppendLine($"{player.Name}: Run {runSkill}, top speed {ceiling:F2} units/s, longest single move allowed {allowance:F1} units")
            .AppendLine($"  at {player.Location?.ToLOCString() ?? "?"}{(Mod.Settings.IsExempt(player) ? "  (staff - never rejected)" : "")}");

        var state = Tracker.Peek(player);

        if (state is null || state.SpeedViolations + state.GeometryViolations == 0)
        {
            text.AppendLine("  no strikes this session");
        }
        else
        {
            text.AppendLine($"  strikes: speed {state.SpeedViolations}, geometry {state.GeometryViolations}, rejected {state.Rejections}, " +
                            $"{state.CountInWindow(DateTime.UtcNow, Mod.Settings.StrikeWindowSeconds)} in the last {Mod.Settings.StrikeWindowSeconds}s");

            if (state.Last is { } v)
                text.AppendLine($"  latest: {Guard.Describe(v)}");
        }

        Reply(session, text.ToString());
    }

    /// <summary>Everyone online with a strike, worst first.</summary>
    private static void Top(Session? session)
    {
        var rows = Tracker.Offenders().Take(15).ToList();

        if (rows.Count == 0)
        {
            Reply(session, "No strikes against anyone currently online.");
            return;
        }

        var text = new StringBuilder("Strikes this session, worst first:\n");

        foreach (var (guid, state) in rows)
        {
            var name = PlayerManager.GetOnlinePlayer(guid)?.Name ?? $"0x{guid:X8}";
            text.AppendLine($"  {name,-24} speed {state.SpeedViolations,4}  geometry {state.GeometryViolations,4}  rejected {state.Rejections,4}");
        }

        Reply(session, text.ToString());
    }

    /// <summary>
    /// The client renders an embedded newline as a music note, so a multi-line
    /// message goes out as one SendMessage per line.
    /// </summary>
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
