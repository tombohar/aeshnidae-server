namespace Aeshnidae.Fellowship;

public static class Commands
{
    [CommandHandler("fellow", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Fellowship size and experience sharing on this server.",
        "/fellow            the share curve, and where your fellowship sits on it\n" +
        "/fellow reload     re-read Settings.json (admin)")]
    public static void HandleFellow(Session session, params string[] parameters)
    {
        if (parameters.Length > 0 && parameters[0].Equals("reload", StringComparison.OrdinalIgnoreCase))
        {
            if (session.AccessLevel < AccessLevel.Admin)
            {
                Reply(session, "Only an admin can reload the settings.");
                return;
            }

            Mod.Reload();
            Reply(session, $"Reloaded. Fellowships up to {AceFellowship.MaxFellows}.");
            return;
        }

        var player = session.Player;
        var mine = player?.Fellowship;
        var size = mine?.FellowshipMembers?.Count ?? 0;

        var sb = new StringBuilder();
        sb.AppendLine($"Fellowships hold up to {AceFellowship.MaxFellows}. " +
                      "Each member's share of earned experience:");

        for (var count = 1; count <= AceFellowship.MaxFellows; count++)
        {
            var here = count == size ? "  <- you" : "";
            sb.AppendLine($"  {count,2} member(s): {Mod.Settings.ShareFor(count),6:P0}{here}");
        }

        if (mine is null)
            sb.Append("You are not in a fellowship.");
        else
            sb.Append($"Yours has {size}, sharing {(mine.ShareXP ? "on" : "off")}" +
                      $"{(mine.EvenShare ? ", split evenly" : "")}.");

        Reply(session, sb.ToString());
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
