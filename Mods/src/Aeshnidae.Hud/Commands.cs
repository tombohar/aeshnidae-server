namespace Aeshnidae.Hud;

/// <summary>
/// /hud on|off|sync - the switch a capable client flips, and the fan-out to the
/// mods that own panels.
///
/// The client says "/hud on" once it is in the world. This mod marks the session as
/// listening and then invokes every provider command (see Feed.ProviderPrefix), each
/// of which sends its own panel. The providers are found in ACE's command registry
/// at call time, so a new panel is a new "hud-x" command in whichever mod owns the
/// numbers, and nothing here changes.
/// </summary>
public static class Commands
{
    [CommandHandler("hud", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Server-drawn panels, for clients that can show them (OpenAC with the Aeshnidae HUD plugin).",
        "/hud on|off|sync")]
    public static void HandleHud(Session session, params string[] parameters)
    {
        var player = session.Player;

        if (player is null)
            return;

        var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "status";

        switch (verb)
        {
            case "on":
                player.SetProperty(Feed.Listening, true);
                // One line per login is cheap, and it is the only trace a client that
                // draws nothing leaves: did the server ever hear from it?
                ModManager.Log($"[{Mod.Name}] {player.Name} turned the HUD feed on");
                Sync(session);
                break;

            case "off":
                player.RemoveProperty(Feed.Listening);
                player.SendMessage("HUD feed off.");
                break;

            case "sync":
                if (Feed.IsOn(player))
                    Sync(session);
                else
                    player.SendMessage("HUD feed is off - /hud on first.");
                break;

            default:
                player.SendMessage($"HUD feed is {(Feed.IsOn(player) ? "on" : "off")}. " +
                                   "It only does anything in a client that can draw the panels.");
                break;
        }
    }

    /// <summary>
    /// Ask every provider to send its panel. One provider throwing must not stop the
    /// rest, so each is called on its own and logged on its own.
    /// </summary>
    private static void Sync(Session session)
    {
        var providers = CommandManager.GetCommands()
            .Where(c => c.Attribute.Command.StartsWith(Feed.ProviderPrefix, StringComparison.Ordinal))
            .OrderBy(c => c.Attribute.Command, StringComparer.Ordinal)
            .ToList();

        ModManager.Log($"[{Mod.Name}] sync for {session.Player?.Name}: {string.Join(", ", providers.Select(p => "/" + p.Attribute.Command))}");

        foreach (var provider in providers)
        {
            try
            {
                ((CommandHandler)provider.Handler)(session, "sync");
            }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] provider /{provider.Attribute.Command} failed for {session.Player?.Name}: {ex}",
                               ModManager.LogLevel.Warn);
            }
        }
    }
}
