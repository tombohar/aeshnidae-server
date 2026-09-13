namespace Aeshnidae.Enlightenment;

/// <summary>
/// Read-only, on purpose.
///
/// The brief for this mod was "enlighten via an NPC", because an enlightenment
/// command had been abusable before. So nothing in this file grants, resets or
/// awards anything - the only route to <see cref="Ritual.Perform"/> is the Font's
/// emote, and there is no admin override here either. If one is ever wanted it
/// should be added deliberately, at AccessLevel.Admin, and logged; it should not
/// arrive by accident because a status command grew a second argument.
/// </summary>
public static class Commands
{
    [CommandHandler("enlighten", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, 0,
        "Show your progress towards enlightenment, and what it would cost.",
        "/enlighten")]
    public static void HandleEnlighten(Session session, params string[] parameters)
    {
        if (session?.Player is null)
            return;

        Reply(session, Requirements.Describe(session.Player));
    }

    /// <summary>
    /// The one write-capable command here, and it writes prerequisites, not results.
    /// See Prep.cs. Targets yourself by default, or a named online player.
    /// </summary>
    [CommandHandler("enlighten-prep", AccessLevel.Admin, CommandHandlerFlag.RequiresWorld, 0,
        "TESTING: give a character every prerequisite for enlightenment (level, auras, society master). Does not enlighten.",
        "/enlighten-prep [player name]")]
    public static void HandlePrep(Session session, params string[] parameters)
    {
        var admin = session?.Player;

        if (admin is null)
            return;

        var target = admin;

        if (parameters.Length > 0)
        {
            var name = string.Join(" ", parameters);
            target = PlayerManager.GetOnlinePlayer(name);

            if (target is null)
            {
                Reply(session, $"No online player called '{name}'.");
                return;
            }
        }

        try
        {
            var report = Prep.Run(admin, target);
            Reply(session, report);

            if (target != admin)
                target.SendMessage($"{admin.Name} has prepared you for enlightenment. /enlighten to see where you stand.");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] enlighten-prep failed for {target.Name}: {ex}", ModManager.LogLevel.Error);
            Reply(session, $"Prep failed: {ex.Message}. See the server log.");
        }
    }

    [CommandHandler("enlightenment-reload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Restart Aeshnidae.Enlightenment, re-reading Settings.json.",
        "/enlightenment-reload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;

        if (container is null)
        {
            Reply(session, $"{Mod.Name} is not loaded.");
            return;
        }

        container.Restart();
        Reply(session, $"{Mod.Name} restarted.");
    }

    [CommandHandler("enlightenment-rules", AccessLevel.Advocate, CommandHandlerFlag.None, 0,
        "Print the enlightenment rules currently in force.",
        "/enlightenment-rules")]
    public static void HandleRules(Session session, params string[] parameters)
    {
        var settings = Mod.Settings;

        var sb = new StringBuilder()
            .AppendLine($"{Mod.Name} - {(settings.Enabled ? "active" : "disabled, retail rules in force")}")
            .AppendLine($"  level required   : {settings.BaseLevelRequirement} +{settings.LevelRequirementPerEnlightenment} per enlightenment held")
            .AppendLine($"  maximum          : {(settings.MaxEnlightenments > 0 ? settings.MaxEnlightenments.ToString() : "uncapped")}")
            .AppendLine($"  also required    : {(settings.RequireSocietyMaster ? "society master" : "-")}, " +
                        $"{(settings.RequireAllLuminanceAuras ? "all luminance auras" : "-")}, " +
                        $"{settings.RequiredFreeInventorySlots} free slots")
            .AppendLine($"  keeps            : society {On(settings.KeepSociety)}, auras {On(settings.KeepLuminanceAuras)}, " +
                        $"lum access {On(settings.KeepLuminanceAccess)}, lum balance {On(settings.KeepLuminanceBalance)}, " +
                        $"unassigned xp {On(settings.KeepUnassignedExperience)}, aetheria {On(settings.KeepAetheria)}")
            .AppendLine($"  resets           : level {On(settings.ResetLevel)}, skills {On(settings.ResetSkills)}, " +
                        $"attributes {On(settings.ResetAttributes)}, dequip {On(settings.DequipAllItems)}")
            .AppendLine($"  grants           : titles {On(settings.GrantTitles)} (five exist), " +
                        $"attribute certificate {On(settings.GrantAttributeResetCertificate)}, " +
                        $"broadcast {On(settings.BroadcastToServer)}")
            .AppendLine($"  re-entry guard   : {settings.ReentryGuardSeconds:N0}s")
            .AppendLine("  +1 all skills and +2 vitality per enlightenment come from ACE itself, not this mod.");

        Reply(session, sb.ToString());
    }

    private static string On(bool value) => value ? "yes" : "no";

    /// <summary>
    /// The client draws an embedded newline as a music note, so multi-line output
    /// has to go one SendMessage per line.
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
