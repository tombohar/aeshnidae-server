namespace Aeshnidae.RemoteConsole;

/// <summary>
/// A console for a server that has none.
///
/// Aeshnidae runs under systemd with ACE_NONINTERACTIVE_CONSOLE=true, so ACE never
/// starts its command prompt and there is no stdin to type into. Until now the only
/// way to run a console command was to log in as a Developer and type it in chat -
/// which meant every content push ended with "and now YOU run this in game", and the
/// pipeline could never be finished by a script.
///
/// This watches a directory. Drop a file of commands in it, one per line, and they are
/// executed exactly the way CommandManager.CommandThread would have executed them from
/// the keyboard: ParseCommand, GetCommandHandler with a null session, Invoke. Nothing
/// is reimplemented; the same handlers, the same access rules. A null session means
/// commands flagged RequiresWorld are refused, as they are on the real console.
///
/// Output goes where console output always went - the log, which systemd captures in
/// the journal. The receipt written beside the command file carries the start and end
/// timestamps precisely so a caller can pull that slice of the journal and read what
/// the command said. Capturing the output in-process was considered and rejected: it
/// would need a log4net appender, a dependency this mod does not otherwise have, to
/// reproduce something journalctl already does.
///
/// Security is the directory. Anyone who can write a file there can run any console
/// command, so it lives under the mod folder, owned by the account the server runs
/// as, and is reachable only by someone who already has ssh to the box. That is the
/// boundary that already protects everything else.
/// </summary>
public static class Inbox
{
    private static Timer? _timer;
    private static string _dir = "";
    private static int _busy;

    public static string Directory => _dir;

    /// <summary>
    /// Whether the command table has been built yet. See Poll.
    ///
    /// Checks for one of ACE's OWN commands, not this mod's. ModContainer registers a
    /// mod's commands the moment the mod is enabled, which is near the start of
    /// startup - so "is remoteconsole registered" is true ten seconds before "mod" or
    /// "clearcache" exist, and a gate on it lets files through into an empty table.
    /// "acecommands" lives in ACE.Server and is only added by CommandManager.Initialize,
    /// which is the event actually being waited for.
    /// </summary>
    public static bool Ready
    {
        get
        {
            try { return CommandManager.GetCommandByName("acecommands").Any(); }
            catch { return false; }
        }
    }

    public static void Start()
    {
        var configured = Mod.Settings.InboxDirectory;

        _dir = Path.IsPathRooted(configured) ? configured : Path.Combine(Mod.ModPath, configured);

        System.IO.Directory.CreateDirectory(_dir);

        var period = TimeSpan.FromSeconds(Math.Max(0.25, Mod.Settings.PollSeconds));

        _timer = new Timer(_ => Poll(), null, period, period);

        ModManager.Log($"[{Mod.Name}] watching {_dir} - drop <name>.cmd there, one command per line");
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>
    /// One pass over the inbox. Re-entrancy guarded, because a command can legitimately
    /// take longer than the poll interval (a landblock walk, say) and a second timer
    /// tick must not start executing the next file on top of it.
    /// </summary>
    private static void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;

        try
        {
            // ACE registers commands LAST in startup - CommandManager.Initialize runs
            // after every landblock and every mod is up - while this mod starts polling
            // the moment it is enabled, near the beginning. A file dropped during those
            // seconds would be claimed and every line refused as "no such command". So
            // nothing is claimed until this mod's own command is registered, which is
            // proof that both CommandManager.Initialize and ModManager.RegisterCommands
            // have run. Files simply wait in the inbox until then.
            if (!Ready)
                return;

            // Name order is execution order - a caller that needs sequencing names
            // the files accordingly.
            foreach (var path in System.IO.Directory.GetFiles(_dir, "*.cmd").OrderBy(p => p, StringComparer.Ordinal))
                Run(path);

            Sweep();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] inbox poll failed: {ex}", ModManager.LogLevel.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private static void Run(string cmdPath)
    {
        // Claim it first. A rename is atomic, so a second poll (or a second server,
        // if that ever happens) cannot pick the same file up.
        var running = Path.ChangeExtension(cmdPath, ".running");

        try
        {
            File.Move(cmdPath, running);
        }
        catch (IOException)
        {
            return;   // somebody else got it, or it vanished
        }

        var receipt = new StringBuilder();
        var started = DateTime.UtcNow;

        receipt.AppendLine($"started {started:O}");

        string[] lines;

        try
        {
            lines = File.ReadAllLines(running);
        }
        catch (Exception ex)
        {
            receipt.AppendLine($"unreadable: {ex.Message}");
            Finish(running, receipt, started);
            return;
        }

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            receipt.AppendLine($"> {line}");
            receipt.AppendLine($"  {Execute(line)}");
        }

        Finish(running, receipt, started);
    }

    /// <summary>
    /// CommandManager.CommandThread, one line at a time. Every branch that method
    /// takes is taken here, so a command behaves identically to having been typed.
    /// </summary>
    private static string Execute(string commandLine)
    {
        // Typing a leading slash is muscle memory from chat; the console never wanted
        // one, so strip it rather than fail on the most likely mistake.
        if (commandLine.StartsWith('/') || commandLine.StartsWith('@'))
            commandLine = commandLine[1..];

        string command;
        string[] parameters;

        try
        {
            CommandManager.ParseCommand(commandLine, out command, out parameters);
        }
        catch (Exception ex)
        {
            return $"could not parse: {ex.Message}";
        }

        CommandHandlerResponse response;
        CommandHandlerInfo? info;

        try
        {
            response = CommandManager.GetCommandHandler(null, command, parameters, out info);
        }
        catch (Exception ex)
        {
            return $"could not resolve: {ex.Message}";
        }

        if (response != CommandHandlerResponse.Ok || info is null)
            return response switch
            {
                CommandHandlerResponse.InvalidCommand => "no such command",
                CommandHandlerResponse.NotInWorld => "needs a player in the world - cannot run from the console",
                CommandHandlerResponse.NotAuthorized => "not authorized",
                CommandHandlerResponse.NoConsoleInvoke => "this command refuses the console",
                CommandHandlerResponse.InvalidParameterCount => "wrong number of parameters",
                _ => $"refused: {response}",
            };

        try
        {
            if (info.Attribute.IncludeRaw)
                parameters = CommandManager.StuffRawIntoParameters(commandLine, command, parameters);

            ((CommandHandler)info.Handler).Invoke(null, parameters);

            return "ok";
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] '{commandLine}' threw: {ex}", ModManager.LogLevel.Error);
            return $"threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Writes the receipt and removes the claim. The receipt is written to a temp name
    /// and renamed, so a caller polling for .done never reads a half-written one.
    /// </summary>
    private static void Finish(string running, StringBuilder receipt, DateTime started)
    {
        var finished = DateTime.UtcNow;

        receipt.AppendLine($"finished {finished:O}");
        receipt.AppendLine($"journal: journalctl -u ace --since \"{started:yyyy-MM-dd HH:mm:ss}\" --until \"{finished.AddSeconds(1):yyyy-MM-dd HH:mm:ss}\" --utc");

        var done = Path.ChangeExtension(running, ".done");
        var temp = done + ".tmp";

        try
        {
            File.WriteAllText(temp, receipt.ToString());
            File.Move(temp, done, overwrite: true);
            File.Delete(running);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not write receipt for {Path.GetFileName(running)}: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }
    }

    /// <summary>Old receipts are noise. Anything else in the directory is left alone.</summary>
    private static void Sweep()
    {
        var minutes = Mod.Settings.ReceiptLifetimeMinutes;

        if (minutes <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddMinutes(-minutes);

        foreach (var path in System.IO.Directory.GetFiles(_dir, "*.done"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < cutoff)
                    File.Delete(path);
            }
            catch
            {
                // a receipt that will not delete is not worth a log line
            }
        }
    }
}
