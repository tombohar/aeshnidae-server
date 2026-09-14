namespace Aeshnidae.Bank;

/// <summary>
/// /bank and /b.
///
/// The item name can be several words ("legendary key"), so parsing works from
/// both ends: first argument is the verb, last is the amount, and everything
/// between is joined back into the item name. That makes
///     /bank deposit legendary key 50
///     /b d lk 50
/// two spellings of the same thing without needing quotes.
/// </summary>
public static class Commands
{
    [CommandHandler("bank", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Deposit and withdraw pyreals, luminance and keys. Shared across your account.",
        "/bank                       show balances\n" +
        "/bank deposit <item> <n>    deposit, or 'all' for everything\n" +
        "/bank withdraw <item> <n>   withdraw\n" +
        "/bank help                  item names and short forms")]
    public static void HandleBank(Session session, params string[] parameters)
    {
        var player = session?.Player;

        if (player is null)
            return;

        if (!BankDb.Ready)
        {
            Send(player, "The bank is not open - its storage failed to initialise. Check the server log.");
            return;
        }

        try
        {
            Run(player, parameters ?? Array.Empty<string>());
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] /bank failed for {player.Name}: {ex}", ModManager.LogLevel.Error);
            Send(player, "Something went wrong at the bank. Nothing was changed.");
        }

        // Whatever the verb did, a listening client redraws its bank panel from the
        // balances as they now are. Cheap, and simpler than tracking which verbs move money.
        Hud.Refresh(player);
    }

    [CommandHandler("b", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "Short form of /bank.", "/b d lk 50")]
    public static void HandleBankShort(Session session, params string[] parameters) =>
        HandleBank(session, parameters);

    private static void Run(Player player, string[] args)
    {
        if (args.Length == 0)
        {
            Send(player, BankService.Statement(player));
            return;
        }

        var verb = args[0].ToLowerInvariant();

        switch (verb)
        {
            case "help" or "h" or "?":
                Send(player, HelpText());
                return;

            case "balance" or "bal" or "b" or "list" or "l":
                Send(player, BankService.Statement(player));
                return;

            case "pay" or "send" or "give":
                Pay(player, args);
                return;

            case "earned" or "rate" or "e":
                Send(player, EarnedText(player));
                return;

            case "autolum" or "autoluminance" or "auto":
                AutoLuminance(player, args);
                return;

            case "deposit" or "dep" or "d":
            case "withdraw" or "wd" or "w":
                break;

            default:
                Send(player, $"Unknown command '{args[0]}'. Try /bank help.");
                return;
        }

        var depositing = verb is "deposit" or "dep" or "d";

        // /b d on its own: everything the bank takes, in one go.
        if (depositing && args.Length == 1)
        {
            Send(player, BankService.DepositAll(player).Message);
            return;
        }

        if (args.Length < 2)
        {
            Send(player, "Usage: /b w <item> [amount]   e.g. /b w p 50k   or   /b w p   for all of it");
            return;
        }

        // The amount is the last token IF it parses as one; otherwise every token after
        // the verb is the item name and the amount is "all". So `/b d lk`, `/b d lk all`
        // and `/b d legendary key` all mean the same thing, and `/b d lk 50` means fifty.
        // Item names first, amount last, because the names can be several words.
        long amount = BankService.All;
        string itemName;

        if (args.Length >= 3 && TryParseAmount(args[^1], out var parsed))
        {
            amount = parsed;
            itemName = string.Join(' ', args[1..^1]);
        }
        else if (args.Length == 2 && TryParseAmount(args[1], out _))
        {
            Send(player, $"Usage: /b {(depositing ? "d" : "w")} <item> [amount] - which item?");
            return;
        }
        else
        {
            itemName = string.Join(' ', args[1..]);
        }

        if (!Currencies.TryResolve(itemName, out var kind))
        {
            Send(player, $"Don't know what '{itemName}' is. Try /bank help for the list.");
            return;
        }

        var result = depositing
            ? BankService.Deposit(player, kind, amount)
            : BankService.Withdraw(player, kind, amount);

        Send(player, result.Message);
    }

    /// <summary>
    /// /bank pay &lt;player&gt; &lt;currency&gt; &lt;amount&gt;
    ///
    /// Parsed from the right: the amount is the last token, the currency the one before
    /// it, and the recipient is everything in between - because character names can be
    /// two words ("Dargoth Hera") and the only earned currencies, the ones that can be
    /// sent, all have one-word names and aliases. Reading the name first would have
    /// swallowed the second word of a two-word name into the currency.
    /// </summary>
    private static void Pay(Player player, string[] args)
    {
        if (args.Length < 4)
        {
            Send(player, "Usage: /b pay <player> <currency> <amount>   e.g. /b pay Dargoth Hera rad 5m");
            return;
        }

        var amountToken = args[^1];
        var currencyName = args[^2];
        var recipient = string.Join(' ', args[1..^2]);

        if (!Currencies.TryResolve(currencyName, out var kind))
        {
            Send(player, $"Don't know what '{currencyName}' is. Try /bank help for the list.");
            return;
        }

        if (!TryParseAmount(amountToken, out var amount) || amount <= 0)
        {
            Send(player, $"'{amountToken}' is not an amount to send. Use a whole number.");
            return;
        }

        Send(player, Transfer.Send(player, recipient, kind, amount).Message);
    }

    /// <summary>
    /// /grant &lt;radiance|resonance&gt; &lt;amount&gt; [player] - Admin, for testing.
    ///
    /// Writes the account balance directly and synchronously: no earning buffer, no
    /// /earned history (it is not earned), no transfer fee. A negative amount takes it
    /// away, and the guarded UPDATE refuses to go below zero. Works from the console
    /// with a player named; in game the player defaults to yourself.
    ///
    /// Only the earned currencies. Pyreals and keys have item forms and the normal
    /// admin commands for those already exist.
    /// </summary>
    [CommandHandler("grant", AccessLevel.Admin, CommandHandlerFlag.None, 2,
        "Grant (or remove) Radiance or Resonance on an account, for testing.",
        "/grant radiance 5000000 Aeshna\n" +
        "/grant resonance 100            (yourself, in game)\n" +
        "/grant radiance -5000000 Aeshna  (take it back)")]
    public static void HandleGrant(Session session, params string[] parameters)
    {
        void Say(string text)
        {
            if (session?.Player is not null) Send(session.Player, text);
            else ModManager.Log($"[{Mod.Name}] {text}");
        }

        if (!BankDb.Ready)
        {
            Say("The bank is not open - its storage failed to initialise.");
            return;
        }

        if (parameters.Length < 2)
        {
            Say("Usage: /grant <radiance|resonance> <amount> [player]");
            return;
        }

        if (!Currencies.TryResolve(parameters[0], out var kind) || !Currencies.IsEarned(kind))
        {
            Say($"'{parameters[0]}' is not an earned currency. Radiance or Resonance.");
            return;
        }

        var amountToken = parameters[1];
        var negative = amountToken.StartsWith('-');

        if (!TryParseAmount(amountToken.TrimStart('-'), out var amount) || amount <= 0)
        {
            Say($"'{amountToken}' is not an amount. Use a whole number - 50, 10k, 2.5m.");
            return;
        }

        if (negative)
            amount = -amount;

        uint accountId;
        string targetName;

        if (parameters.Length >= 3)
        {
            // Names can be two words ("Dargoth Hera"), so the name is everything after
            // the amount, not the next token.
            var name = string.Join(' ', parameters[2..]);
            var target = PlayerManager.FindByName(name);

            if (target?.Account is null)
            {
                Say($"No character called '{name}'.");
                return;
            }

            accountId = target.Account.AccountId;
            targetName = target.Name;
        }
        else if (session?.Player?.Account is not null)
        {
            accountId = session.Player.Account.AccountId;
            targetName = session.Player.Name;
        }
        else
        {
            Say("From the console, name the player: /grant radiance 5000000 Aeshna");
            return;
        }

        if (!BankDb.TryAdjust(accountId, kind, amount, out var balance))
        {
            Say($"Refused - that would take {targetName}'s {Currencies.DisplayName(kind)} below zero.");
            return;
        }

        var who = session?.Player?.Name ?? "console";
        var verb = amount >= 0 ? "granted" : "removed";

        ModManager.Log($"[{Mod.Name}] {who} {verb} {Math.Abs(amount):N0} {kind} {(amount >= 0 ? "to" : "from")} {targetName}'s account; balance now {balance:N0}");
        Say($"{Currencies.DisplayName(kind)}: {verb} {Math.Abs(amount):N0} {(amount >= 0 ? "to" : "from")} {targetName}'s account. Balance: {balance:N0}.");

        // Tell them, if they are on. A balance that changes with no message looks like a bug.
        var online = PlayerManager.GetOnlinePlayer(targetName);

        if (online is not null && online != session?.Player)
            online.SendMessage($"An admin {verb} {Math.Abs(amount):N0} {Currencies.DisplayName(kind)}. Balance: {balance:N0}.");
    }

    [CommandHandler("earned", AccessLevel.Player, CommandHandlerFlag.RequiresWorld, -1,
        "What this character has earned recently.",
        "/earned          rolling windows, session total and hourly rate\n" +
        "/earned reset    start the session over from now")]
    public static void HandleEarned(Session session, params string[] parameters)
    {
        if (session.Player is not { } player)
            return;

        var verb = parameters.Length > 0 ? parameters[0].ToLowerInvariant() : "";

        if (verb is "reset" or "clear" or "zero")
        {
            History.StartSession(player);
            Send(player, "Earnings history cleared. The session clock restarts from now.");
            return;
        }

        Send(player, EarnedText(player));
    }

    /// <summary>
    /// Radiance and resonance earned over the four windows, for the CHARACTER rather
    /// than the account - the balance is shared but the earning is not, and "how is this
    /// spot paying" is a question about who is standing in it.
    ///
    /// Earned only. Currency another player sent you is not counted, or two people could
    /// pass the same balance back and forth and both report a superb hourly rate.
    /// </summary>
    private static string EarnedText(Player player)
    {
        int[] windows = { 5, 10, 30, 60 };

        // Every row is built by walking History.Tracked rather than naming currencies,
        // so adding a fourth to that list is all it takes to appear here.
        var sb = new StringBuilder($"Earned by {player.Name} (kills and quests only):\n");

        sb.Append($"  {"",-10}");

        foreach (var kind in History.Tracked)
            sb.Append($"{Currencies.DisplayName(kind),14}");

        sb.AppendLine();

        foreach (var minutes in windows)
        {
            sb.Append($"  {minutes + " min",-10}");

            foreach (var kind in History.Tracked)
                sb.Append($"{History.Earned(player, kind, minutes),14:N0}");

            sb.AppendLine();
        }

        sb.Append($"  {"Session",-10}");

        foreach (var kind in History.Tracked)
            sb.Append($"{History.Session(player, kind),14:N0}");

        sb.AppendLine($"   over {Duration(History.SessionLength(player))}");

        sb.Append($"  {"Per hour",-10}");

        var projected = History.PerHour(player, History.Tracked[0]) is not null;

        foreach (var kind in History.Tracked)
        {
            var rate = History.PerHour(player, kind);
            sb.Append(rate is null ? $"{"-",14}" : $"{rate.Value,14:N0}");
        }

        sb.AppendLine(projected
            ? "   projected"
            : $"   after {(int)History.MinimumForProjection.TotalMinutes} min of play");

        if (History.Tracked.All(kind => History.Session(player, kind) == 0))
            sb.AppendLine("  Nothing yet this session. /earned reset starts the clock over.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>"2h 14m", "43m", "50s" - the largest useful unit, not a full timestamp.</summary>
    private static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1)
            return $"{(int)span.TotalSeconds}s";

        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes}m";

        return $"{(int)span.TotalHours}h {span.Minutes}m";
    }

    /// <summary>
    /// /bank autolum [on|off] - whether THIS character banks its luminance.
    ///
    /// Per character rather than per account on purpose: a main that should never lose
    /// an award to the cap and a mule that should sit on its luminance are the same
    /// account, and the flag has to be able to tell them apart.
    /// </summary>
    private static void AutoLuminance(Player player, string[] args)
    {
        if (!Mod.Settings.AllowLuminanceAutoBank)
        {
            Send(player, "Automatic luminance banking is switched off on this server.");
            return;
        }

        var current = AutoBank.IsOn(player);

        if (args.Length < 2)
        {
            Send(player, current
                ? $"{player.Name} banks luminance automatically. It bypasses your luminance cap, " +
                  "and purchases draw from the bank. /bank autolum off to stop."
                : $"{player.Name} keeps luminance the normal way, capped at your maximum. " +
                  "/bank autolum on to bank it instead.");
            return;
        }

        var wanted = args[1].ToLowerInvariant() switch
        {
            "on" or "yes" or "1" or "true" => true,
            "off" or "no" or "0" or "false" => false,
            _ => (bool?)null,
        };

        if (wanted is null)
        {
            Send(player, "Usage: /bank autolum on   or   /bank autolum off");
            return;
        }

        if (wanted == current)
        {
            Send(player, $"Already {(current ? "on" : "off")}.");
            return;
        }

        if (!AutoBank.Set(player, wanted.Value))
        {
            Send(player, "That could not be saved, so nothing was changed.");
            return;
        }

        Send(player, wanted.Value
            ? "Luminance will now go straight to your bank, past your cap. Purchases draw from it."
            : "Luminance will now accrue on your character again, up to your cap.");

        // Said plainly rather than left to be discovered. Luminance already on the
        // character is not swept up by turning this on, and somebody who assumes it was
        // will find their old balance still sitting there and conclude the flag failed.
        if (wanted.Value && (player.AvailableLuminance ?? 0) > 0)
            Send(player, $"You are still carrying {player.AvailableLuminance:N0} luminance. " +
                         "/bank deposit luminance all moves it in.");
    }

    /// <summary>Accepts 12345, 12,345, 10k, 2.5m and 'all'.</summary>
    private static bool TryParseAmount(string token, out long amount)
    {
        amount = 0;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        token = token.Trim().Replace(",", "").Replace("_", "");

        if (token.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            token.Equals("max", StringComparison.OrdinalIgnoreCase) ||
            token == "*")
        {
            amount = BankService.All;
            return true;
        }

        long multiplier = 1;
        var last = char.ToLowerInvariant(token[^1]);

        if (last is 'k' or 'm' or 'b')
        {
            multiplier = last switch { 'k' => 1_000L, 'm' => 1_000_000L, _ => 1_000_000_000L };
            token = token[..^1];
        }

        if (!decimal.TryParse(token, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out var value))
            return false;

        var scaled = value * multiplier;

        if (scaled < 0 || scaled > long.MaxValue)
            return false;

        amount = (long)scaled;
        return amount > 0;
    }

    [CommandHandler("bankreload", AccessLevel.Admin, CommandHandlerFlag.None, 0,
        "Re-read Aeshnidae.Bank Settings.json.", "/bankreload")]
    public static void HandleReload(Session session, params string[] parameters)
    {
        var container = Mod.Container;
        var msg = container is null ? $"{Mod.Name} is not loaded." : $"{Mod.Name} reloaded.";

        container?.Restart();

        if (session?.Player is not null)
            Send(session.Player, msg);
        else
            ModManager.Log(msg);
    }

    /// <summary>
    /// The client renders an embedded newline as a music note, so a multi-line
    /// message has to go out as one SendMessage per line.
    /// </summary>
    private static void Send(Player player, string message)
    {
        foreach (var line in (message ?? "").Split('\n'))
        {
            var text = line.TrimEnd('\r');

            if (!string.IsNullOrWhiteSpace(text))
                player.SendMessage(text);
        }
    }

    /// <summary>
    /// Short forms only. Every verb has long forms too, but a help screen that lists
    /// both is twice as long and no clearer - the person who wants to type
    /// /bank deposit will find it works without being told.
    /// </summary>
    private static string HelpText() =>
        "Bank - one balance shared by every character on your account.\n" +
        "  /b                    balances\n" +
        "  /b d                  deposit EVERYTHING - all pyreals, luminance and keys\n" +
        "  /b d <item> [n]       deposit one thing; no amount means all of it\n" +
        "  /b w <item> [n]       withdraw; no amount means all of it\n" +
        "  /b pay <who> <cur> <n>   send Radiance or Resonance to another player\n" +
        "  /b autolum on|off     earned luminance goes straight to the bank, past your cap\n" +
        "  /earned               what you have earned lately, and per hour\n" +
        "Items:  p pyreals   l luminance   lk legendary keys   sik sturdy iron keys\n" +
        "        rad radiance   res resonance   (earned, never carried)\n" +
        "Amounts: 50, 10k, 2.5m, all.   Keys bank as uses: a 25-use key is 25.\n" +
        "Radiance mirrors experience from kills and quests; Resonance is 10 per quest.";
}
