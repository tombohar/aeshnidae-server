namespace Aeshnidae.AdminAudit;

/// <summary>
/// Hooks into the other Aeshnidae mods - bank transfers, XP-currency transfers and
/// instance creation.
///
/// These cannot be written as ordinary [HarmonyPatch] classes. Each mod is loaded into
/// its own collectible AssemblyLoadContext, so this assembly cannot reference
/// <c>CurrencyKind</c> or <c>BankResult</c> at compile time - there is no build-time
/// relationship between the mods at all. So the targets are resolved by name at
/// runtime and patched through Harmony's imperative API.
///
/// Three things make that workable, all verified rather than assumed:
///
/// - Harmony binds postfix parameters <i>by name</i>, and only the ones you ask for.
///   Declaring just <c>(Player player, long amount)</c> and simply not mentioning
///   <c>kind</c> sidesteps the untypeable parameter entirely.
/// - <c>object[] __args</c> carries every argument boxed, for the ones that cannot be
///   declared. Enum values arrive as their own type and render correctly via ToString.
/// - <c>object __result</c> works for value-type returns too; Harmony boxes them.
///
/// The one thing that does not work: <c>out</c> parameters read through
/// <c>__args</c> arrive as null. Declare those by name instead, which is possible
/// whenever the type is a shared one - <c>out string message</c> is just a string.
///
/// Targets are resolved through <c>ModManager.GetModContainerByName(...).ModAssembly</c>
/// - never by scanning <c>AppDomain.CurrentDomain.GetAssemblies()</c>. That distinction
/// matters more than it looks: every /mod find loads a fresh copy of each mod into a new
/// context, and the previous copy stays loaded until its context is collected. A scan
/// returns the *oldest* match, so the patch binds to an assembly nothing calls any more
/// and cheerfully reports success. ACE's own container is the only authority on which
/// copy is live.
///
/// The cost of this approach is that a rename in a sibling mod silently drops a hook,
/// and reloading a sibling mod (which /mod find does to all of them) unbinds it. Hence
/// <see cref="Status"/>, surfaced by /adminaudit hooks, and /adminaudit rebind.
/// </summary>
internal static class SiblingPatches
{
    public sealed record HookStatus(string Target, bool Bound, string Note);

    private static readonly List<HookStatus> _status = new();

    public static IReadOnlyList<HookStatus> Status
    {
        get { lock (_status) return _status.ToList(); }
    }

    /// <summary>Landblock copies already seen, so GetOrCreate can tell creation from a lookup.</summary>
    private static readonly HashSet<(ushort Landblock, int Copy)> _knownCopies = new();

    private static Auditor? Audit => Mod.Auditor;

    /// <summary>Methods actually patched, so a re-Apply can detach from the old ones.</summary>
    private static readonly Dictionary<string, MethodBase> _bound = new();

    public static void Apply(Harmony harmony)
    {
        lock (_status)
            _status.Clear();

        _knownCopies.Clear();

        Bind(harmony, "Aeshnidae.Bank", "Aeshnidae.Bank.BankService", "Deposit", nameof(AfterBankMove));
        Bind(harmony, "Aeshnidae.Bank", "Aeshnidae.Bank.BankService", "Withdraw", nameof(AfterBankMove));
        Bind(harmony, "Aeshnidae.Bank", "Aeshnidae.Bank.Transfer", "Send", nameof(AfterBankPay));
        Bind(harmony, "Aeshnidae.XpCurrency", "Aeshnidae.XpCurrency.Transfer", "Send", nameof(AfterXpSend));
        Bind(harmony, "Aeshnidae.InstancesNoDat", "Aeshnidae.InstancesNoDat.InstanceWorld", "GetOrCreate", nameof(AfterGetOrCreate));
    }

    private static void Bind(Harmony harmony, string modName, string typeName, string methodName, string postfix)
    {
        var target = $"{typeName}.{methodName}";

        try
        {
            // Detach from whatever we patched last time, by the MethodInfo we actually
            // used. Unpatching the *newly* resolved method would silently miss a stale
            // binding, and leaving a patch on a dead assembly pins its load context.
            if (_bound.Remove(target, out var previous))
            {
                try { harmony.Unpatch(previous, HarmonyPatchType.Postfix, Mod.HarmonyId); }
                catch { /* the old assembly may already be gone; nothing to detach from */ }
            }

            var container = ModManager.GetModContainerByName(modName, allowPartial: false);

            if (container is null)
            {
                Note(target, false, "mod not installed");
                return;
            }

            if (container.Status != ModStatus.Active)
            {
                Note(target, false, $"mod is {container.Status}");
                return;
            }

            var type = container.ModAssembly?.GetType(typeName, throwOnError: false);

            if (type is null)
            {
                Note(target, false, "type not found in that mod's assembly");
                return;
            }

            var method = AccessTools.Method(type, methodName);

            if (method is null)
            {
                Note(target, false, "method not found - renamed upstream?");
                return;
            }

            harmony.Patch(method, postfix: new HarmonyMethod(typeof(SiblingPatches), postfix));

            _bound[target] = method;
            Note(target, true, "bound");
        }
        catch (Exception ex)
        {
            Note(target, false, $"{ex.GetType().Name}: {ex.Message}");
            ModManager.Log($"[{Mod.Name}] could not hook {target}: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    private static void Note(string target, bool bound, string note)
    {
        lock (_status)
            _status.Add(new HookStatus(target, bound, note));
    }

    // ------------------------------------------------------------------------ bank

    /// <summary>
    /// Serves both Deposit and Withdraw - <c>__originalMethod</c> says which.
    /// <c>kind</c> is deliberately not declared: it is Aeshnidae.Bank's own enum, which
    /// this assembly cannot name. It comes out of <c>__args</c> instead.
    /// </summary>
    public static void AfterBankMove(Player player, long amount, object[] __args, object __result, MethodBase __originalMethod)
    {
        try
        {
            if (Audit is not { } audit || player is null)
                return;

            if (!audit.ShouldAudit(player.Session?.AccessLevel ?? AccessLevel.Player, player.Name))
                return;

            var direction = __originalMethod?.Name ?? "bank";
            var kind = __args is { Length: > 1 } ? __args[1]?.ToString() ?? "?" : "?";

            var (ok, message) = ReadBankResult(__result);

            var record = Auditor.For(player, AuditKind.Bank, direction.ToLowerInvariant());

            record.Target = player.Name;
            record.Outcome = ok ? "ok" : "failed";
            record.Detail = $"{direction.ToLowerInvariant()} {amount:N0} {kind}" +
                            (ok ? "" : $" - refused: {message}");

            record.With("amount", amount).With("currency", kind);

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] bank capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// /b pay - the one bank action that reaches somebody else's balance, and the only
    /// way Radiance and Resonance move between accounts. Unhooked until 2026-09-14: the
    /// hook beside it watched the retired XpCurrency's transfers instead. The currency
    /// is Bank's own enum and comes out of __args, as in AfterBankMove.
    /// </summary>
    public static void AfterBankPay(Player sender, string recipientName, long amount, object[] __args, object __result)
    {
        try
        {
            if (Audit is not { } audit || sender is null)
                return;

            if (!audit.ShouldAudit(sender.Session?.AccessLevel ?? AccessLevel.Player, sender.Name))
                return;

            var kind = __args is { Length: > 2 } ? __args[2]?.ToString() ?? "?" : "?";

            var (ok, message) = ReadBankResult(__result);

            var record = Auditor.For(sender, AuditKind.Bank, "pay");

            record.Target = recipientName;
            record.Outcome = ok ? "ok" : "failed";
            record.Detail = $"pay {amount:N0} {kind} to {recipientName}" + (ok ? "" : $" - refused: {message}");

            record.With("amount", amount).With("currency", kind);

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] bank-pay capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>BankResult is a record in another load context, so it is read reflectively.</summary>
    private static (bool Ok, string Message) ReadBankResult(object? result)
    {
        if (result is null)
            return (false, "no result");

        try
        {
            var type = result.GetType();
            var ok = type.GetProperty("Ok")?.GetValue(result) as bool? ?? false;
            var message = type.GetProperty("Message")?.GetValue(result) as string ?? "";

            return (ok, message);
        }
        catch
        {
            return (false, "unreadable result");
        }
    }

    // ------------------------------------------------------------------ xp currency

    /// <summary>
    /// Every parameter here is a shared type - Player, string, long - so they are all
    /// declared by name, including the out parameter, which __args would report as null.
    /// </summary>
    public static void AfterXpSend(Player sender, string targetName, long amount, ref string message, bool __result)
    {
        try
        {
            if (Audit is not { } audit || sender is null)
                return;

            if (!audit.ShouldAudit(sender.Session?.AccessLevel ?? AccessLevel.Player, sender.Name))
                return;

            var record = Auditor.For(sender, AuditKind.XpTransfer, "xpsend");

            record.Target = targetName;
            record.Outcome = __result ? "ok" : "failed";
            record.Detail = $"sent {amount:N0} XP to {targetName}" + (__result ? "" : $" - refused: {message}");

            record.With("amount", amount);

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] xp-transfer capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // -------------------------------------------------------------------- instances

    /// <summary>
    /// GetOrCreate runs on every entry into an instanced dungeon, not just on creation,
    /// so creation is inferred by keeping our own set of copies seen. Cheaper and more
    /// robust than reflecting into the mod's private dictionary, and being wrong once
    /// after a reload costs at most one duplicate record.
    /// </summary>
    public static void AfterGetOrCreate(ushort landblock, int copy, object __result)
    {
        try
        {
            if (Audit is not { } audit || __result is null)
                return;

            lock (_knownCopies)
            {
                if (!_knownCopies.Add((landblock, copy)))
                    return;
            }

            var frame = CommandContext.Current;

            var record = Auditor.For(null, AuditKind.Instance, frame?.Command ?? "instance");

            record.Actor = frame?.Actor ?? "server";
            record.Account = frame?.Account;
            record.Source = frame is null ? "server" : "ingame";
            record.Outcome = "ok";
            record.Detail = $"created copy {copy} of landblock {landblock:X4}";

            record.With("landblock", $"{landblock:X4}").With("copy", copy);

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] instance capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }
}
