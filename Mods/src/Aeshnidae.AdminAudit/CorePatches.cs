namespace Aeshnidae.AdminAudit;

/// <summary>
/// The hooks into ACE itself. Everything here is compile-time typed; the sibling-mod
/// hooks, which cannot be, live in <see cref="SiblingPatches"/>.
///
/// Every patch is a postfix or a read-only prefix that returns void, so none of them
/// can change what the server does. An audit trail that alters behaviour is worse than
/// no audit trail.
/// </summary>
[HarmonyPatch]
public static class CorePatches
{
    private static Auditor? Audit => Mod.Auditor;

    // ---------------------------------------------------------------- command frame

    /// <summary>
    /// Brackets a whole in-game command. ACE invokes the handler synchronously inside
    /// GameActionTalk.Handle, so anything the handler does happens between these two,
    /// on this thread - which is what lets the outcome hooks below attribute themselves
    /// to a command and ignore ordinary gameplay.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameActionTalk), nameof(GameActionTalk.Handle))]
    public static void PreTalk() => CommandContext.Clear();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameActionTalk), nameof(GameActionTalk.Handle))]
    public static void PostTalk() => CommandContext.Clear();

    // -------------------------------------------------------------- command journal

    /// <summary>
    /// Every command, from the console and from the game, allowed or refused.
    ///
    /// This is ACE's single authorization gate - both callers go through it - which
    /// makes it the one place that sees an attempt regardless of outcome. Refused
    /// attempts are the most interesting records in the whole trail: they are what
    /// someone probing for access looks like.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(CommandManager), nameof(CommandManager.GetCommandHandler))]
    public static void PostGetCommandHandler(Session session, string command, string[] parameters,
                                             ref CommandHandlerInfo commandInfo, CommandHandlerResponse __result)
    {
        try
        {
            if (Audit is not { } audit || command is null)
                return;

            // The console has unrestricted access by definition, so it is always worth
            // recording regardless of the configured threshold.
            var access = session?.AccessLevel ?? AccessLevel.Admin;
            var required = commandInfo?.Attribute?.Access ?? AccessLevel.Player;

            // Record if the actor is privileged, or if a plain player just reached for
            // a command they should not have.
            var interesting = audit.ShouldAudit(access, session?.Player?.Name)
                              || (required > AccessLevel.Player && __result == CommandHandlerResponse.NotAuthorized);

            if (!interesting)
                return;

            // A Player-access command run by an admin is not a use of privilege. Skipping
            // those is what keeps a client plugin polling /b every minute out of the
            // trail; the bank, XP and item hooks still record anything it actually does.
            if (!Mod.Settings.ShouldJournalCommand(command, required, __result == CommandHandlerResponse.NotAuthorized))
            {
                OpenFrame(session, command, __result);
                return;
            }

            var args = parameters is { Length: > 0 } ? " " + string.Join(" ", parameters) : "";
            var readOnly = Mod.Settings.IsReadOnlyCommand(command);

            var record = Auditor.For(session?.Player, AuditKind.Command, command);

            record.Access = access.ToString();
            record.Source = session is null ? "console" : "ingame";
            record.ReadOnly = readOnly;
            record.Outcome = __result == CommandHandlerResponse.Ok ? "ok" : __result.ToString();
            record.Detail = $"@{command}{args}";
            record.Via = null;   // this event IS the command

            record.With("requires", required)
                  .With("sudo", __result == CommandHandlerResponse.SudoOk ? "true" : null);

            audit.Record(record);

            OpenFrame(session, command, __result);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] command journal failed for '{command}': {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// Opens the command frame for the outcome hooks. Only for the in-game path: it is
    /// the only one bracketed by PreTalk/PostTalk, and an unbracketed frame would linger
    /// on the console thread and mis-attribute whatever ran next.
    ///
    /// Called even for commands that are not journalled, so an item conjured by one is
    /// still attributed to the command that conjured it.
    /// </summary>
    private static void OpenFrame(Session? session, string command, CommandHandlerResponse result)
    {
        if (session?.Player is not { } player || result is not (CommandHandlerResponse.Ok or CommandHandlerResponse.SudoOk))
            return;

        CommandContext.Set(new CommandFrame
        {
            Actor = player.Name,
            Account = player.Account?.AccountName,
            Access = session.AccessLevel,
            Command = command,
            Detail = $"@{command}",
            ReadOnly = Mod.Settings.IsReadOnlyCommand(command),
        });
    }

    // ------------------------------------------------------- ACE's own narrated acts

    /// <summary>
    /// ACE already describes 44 privileged actions in prose on its audit channel -
    /// object deletion, smite, teleporting players, server property changes, shutdown.
    /// Rather than re-deriving those, this captures what ACE already decided was worth
    /// announcing. A prefix, so it records whether or not anyone is listening on the
    /// channel in game.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.BroadcastToAuditChannel))]
    public static void PreBroadcastToAuditChannel(Player issuer, string message)
    {
        try
        {
            if (Audit is not { } audit)
                return;

            var access = issuer?.Session?.AccessLevel ?? AccessLevel.Admin;

            if (issuer is not null && !audit.ShouldAudit(access, issuer.Name))
                return;

            var record = Auditor.For(issuer, AuditKind.Narrated, CommandContext.Current?.Command ?? "narrated");
            record.Detail = message ?? "";
            record.Outcome = "ok";

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] audit-channel capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // ------------------------------------------------------------------- item flows

    /// <summary>
    /// An object conjured into an inventory. Gated on an active command frame, which is
    /// what separates "an admin ran /ci" from the 28 other call sites that are ordinary
    /// gameplay - quest rewards, salvage, emote-granted items.
    ///
    /// Patches the two-argument overload only; the one-argument version delegates to it,
    /// so patching both would record every creation twice.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.TryCreateInInventoryWithNetworking),
        new[] { typeof(WorldObject), typeof(Container) },
        new[] { ArgumentType.Normal, ArgumentType.Out })]
    public static void PostTryCreateInInventory(Player __instance, WorldObject item, bool __result)
    {
        try
        {
            if (Audit is not { } audit || item is null)
                return;

            // No command running means this was gameplay, not an admin conjuring.
            if (CommandContext.Current is null)
                return;

            var record = Auditor.For(__instance, AuditKind.ItemCreated, "create");

            record.Target = __instance?.Name;
            record.Outcome = __result ? "ok" : "failed";
            record.Detail = $"created {Describe(item)} in {__instance?.Name}'s inventory";

            record.With("wcid", item.WeenieClassId)
                  .With("guid", $"0x{item.Guid.Full:X8}")
                  .With("stack", item.StackSize ?? 1);

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] item-creation capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>
    /// A privileged character putting something on the ground. Not a command, so this is
    /// filtered on the actor's access level rather than on a command frame.
    ///
    /// The prefix captures the item because by the time the postfix runs it has left the
    /// inventory and the guid no longer resolves.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionDropItem))]
    public static void PreDropItem(Player __instance, uint itemGuid, out string? __state)
    {
        __state = null;

        try
        {
            if (Audit is not { } audit || __instance is null)
                return;

            if (!audit.ShouldAudit(__instance.Session?.AccessLevel ?? AccessLevel.Player, __instance.Name))
                return;

            __state = Describe(__instance.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems));
        }
        catch
        {
            // A drop we cannot describe is still worth recording; the postfix falls back
            // to the raw guid.
            __state = null;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionDropItem))]
    public static void PostDropItem(Player __instance, uint itemGuid, string? __state)
    {
        try
        {
            if (Audit is not { } audit || __state is null)
                return;

            var record = Auditor.For(__instance, AuditKind.ItemDropped, "drop");

            record.Detail = $"dropped {__state}";
            record.With("guid", $"0x{itemGuid:X8}");

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] drop capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>A privileged character handing an object to a player or NPC.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    public static void PreGiveObject(Player __instance, uint targetGuid, uint itemGuid, int amount, out string? __state)
    {
        __state = null;

        try
        {
            if (Audit is not { } audit || __instance is null)
                return;

            if (!audit.ShouldAudit(__instance.Session?.AccessLevel ?? AccessLevel.Player, __instance.Name))
                return;

            var item = __instance.FindObject(itemGuid, Player.SearchLocations.MyInventory | Player.SearchLocations.MyEquippedItems);
            var target = PlayerManager.GetOnlinePlayer(new ObjectGuid(targetGuid));

            __state = $"{(amount > 1 ? $"{amount}x " : "")}{Describe(item)}|{target?.Name ?? $"0x{targetGuid:X8}"}";
        }
        catch
        {
            __state = null;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.HandleActionGiveObjectRequest))]
    public static void PostGiveObject(Player __instance, uint itemGuid, string? __state)
    {
        try
        {
            if (Audit is not { } audit || __state is null)
                return;

            var parts = __state.Split('|');

            var record = Auditor.For(__instance, AuditKind.ItemGiven, "give");

            record.Target = parts.Length > 1 ? parts[1] : null;
            record.Detail = $"gave {parts[0]} to {record.Target}";
            record.With("guid", $"0x{itemGuid:X8}");

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] give capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    // ---------------------------------------------------------------------- grants

    /// <summary>
    /// XP granted by command. <see cref="XpType.Admin"/> is the discriminator: it
    /// appears in exactly two places in all of ACE, both of them the grant commands, so
    /// this costs nothing on the gameplay path even though GrantXP itself is called on
    /// every kill, proficiency tick and fellowship split.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.GrantXP))]
    public static void PostGrantXP(Player __instance, long amount, XpType xpType) =>
        RecordGrant(__instance, amount, xpType, "xp");

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Player), nameof(Player.GrantLuminance))]
    public static void PostGrantLuminance(Player __instance, long amount, XpType xpType) =>
        RecordGrant(__instance, amount, xpType, "luminance");

    private static void RecordGrant(Player? recipient, long amount, XpType xpType, string currency)
    {
        try
        {
            if (Audit is not { } audit || xpType != XpType.Admin)
                return;

            var frame = CommandContext.Current;

            // The recipient is the patched instance; the admin who ran the command is in
            // the frame. They are usually different players, and the trail needs both.
            var record = Auditor.For(recipient, AuditKind.Grant, frame?.Command ?? $"grant{currency}");

            record.Target = recipient?.Name;
            record.Detail = $"{frame?.Actor ?? "someone"} granted {amount:N0} {currency} to {recipient?.Name}";
            record.Outcome = "ok";

            record.With("amount", amount).With("currency", currency);

            if (frame is not null)
            {
                record.Actor = frame.Actor;
                record.Account = frame.Account;
                record.Access = frame.Access.ToString();
            }

            audit.Record(record);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] grant capture failed: {ex.Message}", ModManager.LogLevel.Error);
        }
    }

    /// <summary>"Gold Scarab (0x50001234, wcid 30977)", or a guid when the object is gone.</summary>
    internal static string Describe(WorldObject? item) =>
        item is null
            ? "an object"
            : $"{item.Name}{(item.StackSize > 1 ? $" x{item.StackSize}" : "")} (0x{item.Guid.Full:X8}, wcid {item.WeenieClassId})";
}
