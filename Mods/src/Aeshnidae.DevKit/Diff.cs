using ACE.Database.Models.World;
using System.Globalization;

namespace Aeshnidae.DevKit;

/// <summary>
/// "What did I change?" - a weenie or a landblock in this server's world schema
/// compared with the same one in the base schema (the untouched copy of live that
/// staging was cloned from).
///
/// Rows are compared by CONTENT, never by id. A weenie that was exported, edited and
/// re-imported has been deleted and re-inserted, so every autoincrement id under it is
/// new even where nothing changed; comparing ids would call every row modified. The
/// emote tables are the awkward case - actions hang off their emote's id - so each
/// emote is folded together with its actions into one composite before comparing.
///
/// Output is lines in the same vocabulary as /winfo, prefixed
///   ~  changed     -  removed     +  added
/// </summary>
internal static class Diff
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public sealed record Result(string Subject, bool InBase, bool InWorld, List<string> Lines)
    {
        public bool Changed => Lines.Count > 0;
    }

    /// <summary>Tables whose rows are (type, value) pairs; rendered as property changes.</summary>
    private static readonly Dictionary<string, string> PropertyTables = new()
    {
        ["weenie_properties_int"] = "int",
        ["weenie_properties_int64"] = "int64",
        ["weenie_properties_bool"] = "bool",
        ["weenie_properties_float"] = "float",
        ["weenie_properties_string"] = "string",
        ["weenie_properties_d_i_d"] = "d_i_d",
        ["weenie_properties_i_i_d"] = "i_i_d",
    };

    private static readonly string[] IgnoredColumns = { "id", "object_Id", "last_Modified" };

    // ---- weenie -----------------------------------------------------------------

    public static Result Weenie(uint wcid)
    {
        var world = Db.SafeIdent(Db.WorldSchema);
        var baseS = Db.SafeIdent(Mod.Settings.BaseSchema);

        var lines = new List<string>();

        var headBase = Db.Query($"SELECT class_Name, type FROM `{baseS}`.weenie WHERE class_Id = @w", ("@w", wcid)).FirstOrDefault();
        var headWorld = Db.Query($"SELECT class_Name, type FROM `{world}`.weenie WHERE class_Id = @w", ("@w", wcid)).FirstOrDefault();

        var subject = $"{wcid} {Names.Weenie(wcid)}";

        if (headBase is null && headWorld is null)
            return new Result(subject, false, false, lines);

        if (headBase is null)
        {
            lines.Add($"+ weenie {wcid} is NEW - not in live");
        }
        else if (headWorld is null)
        {
            lines.Add($"- weenie {wcid} DELETED - exists in live");
            return new Result(subject, true, false, lines);
        }
        else
        {
            if (!Equals(headBase["class_Name"], headWorld["class_Name"]))
                lines.Add($"~ class name: {headBase["class_Name"]} -> {headWorld["class_Name"]}");
            if (Convert.ToUInt32(headBase["type"]) != Convert.ToUInt32(headWorld["type"]))
                lines.Add($"~ weenie type: {Format.EnumName<WeenieType>(Convert.ToUInt32(headBase["type"]))} -> {Format.EnumName<WeenieType>(Convert.ToUInt32(headWorld["type"]))}");
        }

        var tables = Db.Query(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @s AND table_name LIKE 'weenie\\_properties\\_%' ORDER BY table_name",
                ("@s", world))
            .Select(r => (string)r["table_name"]!)
            .Where(t => t != "weenie_properties_emote_action")
            .ToList();

        foreach (var table in tables)
        {
            if (table == "weenie_properties_emote")
            {
                DiffEmotes(lines, baseS, world, wcid);
                continue;
            }

            var cols = Db.Columns(world, table, IgnoredColumns);
            if (cols.Count == 0) continue;

            var colList = string.Join(", ", cols.Select(c => $"`{c}`"));
            var baseRows = headBase is null ? new List<Db.Row>() : Db.Query($"SELECT {colList} FROM `{baseS}`.`{table}` WHERE object_Id = @w", ("@w", wcid));
            var worldRows = Db.Query($"SELECT {colList} FROM `{world}`.`{table}` WHERE object_Id = @w", ("@w", wcid));

            var (removed, added) = Compare(baseRows, worldRows, cols);
            if (removed.Count == 0 && added.Count == 0) continue;

            if (PropertyTables.TryGetValue(table, out var kind))
                RenderProperties(lines, kind, removed, added);
            else
                RenderRows(lines, table, removed, added);
        }

        return new Result(subject, headBase is not null, true, lines);
    }

    private static void DiffEmotes(List<string> lines, string baseS, string world, uint wcid)
    {
        var baseEmotes = LoadEmotes(baseS, wcid);
        var worldEmotes = LoadEmotes(world, wcid);

        // Composite key: the emote's own columns plus each action's columns in order.
        var baseKeys = baseEmotes.Select(e => (key: e.key, emote: e.emote)).ToList();
        var worldKeys = worldEmotes.Select(e => (key: e.key, emote: e.emote)).ToList();

        var removed = MultisetExcept(baseKeys, worldKeys, x => x.key);
        var added = MultisetExcept(worldKeys, baseKeys, x => x.key);

        // Pair a removed and an added emote with the same trigger so it reads as a change.
        foreach (var r in removed.ToList())
        {
            var header = Format.EmoteHeader(r.emote);
            var match = added.FirstOrDefault(a => Format.EmoteHeader(a.emote) == header);
            if (match.emote is null) continue;

            lines.Add($"~ {header}:");
            lines.Add($"    was: {string.Join(" ; ", r.emote.WeeniePropertiesEmoteAction.OrderBy(a => a.Order).Select(Format.Action))}");
            lines.Add($"    now: {string.Join(" ; ", match.emote.WeeniePropertiesEmoteAction.OrderBy(a => a.Order).Select(Format.Action))}");
            removed.Remove(r);
            added.Remove(match);
        }

        foreach (var r in removed) lines.Add($"- emote {Format.Emote(r.emote)}");
        foreach (var a in added) lines.Add($"+ emote {Format.Emote(a.emote)}");
    }

    private static List<(string key, WeeniePropertiesEmote emote)> LoadEmotes(string schema, uint wcid)
    {
        var result = new List<(string, WeeniePropertiesEmote)>();

        var emoteCols = Db.Columns(schema, "weenie_properties_emote", IgnoredColumns);
        var actionCols = Db.Columns(schema, "weenie_properties_emote_action", "id", "emote_Id");

        var emotes = Db.Query($"SELECT id, {string.Join(", ", emoteCols.Select(c => $"`{c}`"))} FROM `{schema}`.weenie_properties_emote WHERE object_Id = @w ORDER BY id", ("@w", wcid));
        if (emotes.Count == 0) return result;

        var ids = string.Join(",", emotes.Select(e => Convert.ToUInt32(e["id"]).ToString(Inv)));
        var actions = Db.Query($"SELECT emote_Id, {string.Join(", ", actionCols.Select(c => $"`{c}`"))} FROM `{schema}`.weenie_properties_emote_action WHERE emote_Id IN ({ids}) ORDER BY emote_Id, `order`")
            .GroupBy(a => Convert.ToUInt32(a["emote_Id"]))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var row in emotes)
        {
            var id = Convert.ToUInt32(row["id"]);
            actions.TryGetValue(id, out var acts);
            acts ??= new List<Db.Row>();

            var e = new WeeniePropertiesEmote
            {
                ObjectId = wcid,
                Category = Convert.ToUInt32(row["category"]),
                Probability = Convert.ToSingle(row["probability"]),
                WeenieClassId = U(row["weenie_Class_Id"]),
                Style = U(row["style"]),
                Substyle = U(row["substyle"]),
                Quest = row["quest"] as string,
                VendorType = I(row["vendor_Type"]),
                MinHealth = F(row["min_Health"]),
                MaxHealth = F(row["max_Health"]),
            };

            foreach (var a in acts)
            {
                e.WeeniePropertiesEmoteAction.Add(new WeeniePropertiesEmoteAction
                {
                    Order = Convert.ToUInt32(a["order"]),
                    Type = Convert.ToUInt32(a["type"]),
                    Delay = Convert.ToSingle(a["delay"]),
                    Extent = Convert.ToSingle(a["extent"]),
                    Motion = U(a["motion"]),
                    Message = a["message"] as string,
                    TestString = a["test_String"] as string,
                    Min = I(a["min"]),
                    Max = I(a["max"]),
                    Min64 = L(a["min_64"]),
                    Max64 = L(a["max_64"]),
                    MinDbl = D(a["min_Dbl"]),
                    MaxDbl = D(a["max_Dbl"]),
                    Stat = I(a["stat"]),
                    Display = a["display"] is null ? null : Convert.ToBoolean(a["display"]),
                    Amount = I(a["amount"]),
                    Amount64 = L(a["amount_64"]),
                    HeroXP64 = L(a["hero_X_P_64"]),
                    Percent = D(a["percent"]),
                    SpellId = I(a["spell_Id"]),
                    WealthRating = I(a["wealth_Rating"]),
                    TreasureClass = I(a["treasure_Class"]),
                    TreasureType = I(a["treasure_Type"]),
                    PScript = I(a["p_Script"]),
                    Sound = I(a["sound"]),
                    DestinationType = a["destination_Type"] is null ? null : Convert.ToSByte(a["destination_Type"]),
                    WeenieClassId = U(a["weenie_Class_Id"]),
                    StackSize = I(a["stack_Size"]),
                    Palette = I(a["palette"]),
                    Shade = F(a["shade"]),
                    TryToBond = a["try_To_Bond"] is null ? null : Convert.ToBoolean(a["try_To_Bond"]),
                    ObjCellId = U(a["obj_Cell_Id"]),
                    OriginX = F(a["origin_X"]),
                    OriginY = F(a["origin_Y"]),
                    OriginZ = F(a["origin_Z"]),
                    AnglesW = F(a["angles_W"]),
                    AnglesX = F(a["angles_X"]),
                    AnglesY = F(a["angles_Y"]),
                    AnglesZ = F(a["angles_Z"]),
                });
            }

            var key = Normalize(row, emoteCols) + "||" + string.Join("|", acts.Select(a => Normalize(a, actionCols)));
            result.Add((key, e));
        }

        return result;
    }

    // ---- landblock ------------------------------------------------------------------

    public static Result Landblock(ushort landblock)
    {
        var world = Db.SafeIdent(Db.WorldSchema);
        var baseS = Db.SafeIdent(Mod.Settings.BaseSchema);
        var lines = new List<string>();

        var baseRows = LoadInstances(baseS, landblock);
        var worldRows = LoadInstances(world, landblock);

        var subject = $"landblock {landblock:X4}";

        if (baseRows.Count == 0 && worldRows.Count == 0)
            return new Result(subject, false, false, lines);

        foreach (var (guid, b) in baseRows)
        {
            if (!worldRows.TryGetValue(guid, out var w))
            {
                lines.Add($"- {Format.Instance(b)}");
                continue;
            }

            var changes = new List<string>();
            if (b.WeenieClassId != w.WeenieClassId) changes.Add($"weenie <{Names.WeenieRef(b.WeenieClassId)}> -> <{Names.WeenieRef(w.WeenieClassId)}>");
            if (b.ObjCellId != w.ObjCellId) changes.Add($"cell 0x{b.ObjCellId:X8} -> 0x{w.ObjCellId:X8}");
            if (!Near(b.OriginX, w.OriginX) || !Near(b.OriginY, w.OriginY) || !Near(b.OriginZ, w.OriginZ))
                changes.Add($"moved ({b.OriginX:0.###}, {b.OriginY:0.###}, {b.OriginZ:0.###}) -> ({w.OriginX:0.###}, {w.OriginY:0.###}, {w.OriginZ:0.###})");
            if (!Near(b.AnglesW, w.AnglesW) || !Near(b.AnglesX, w.AnglesX) || !Near(b.AnglesY, w.AnglesY) || !Near(b.AnglesZ, w.AnglesZ))
                changes.Add($"rotated ({b.AnglesW:0.###}, {b.AnglesX:0.###}, {b.AnglesY:0.###}, {b.AnglesZ:0.###}) -> ({w.AnglesW:0.###}, {w.AnglesX:0.###}, {w.AnglesY:0.###}, {w.AnglesZ:0.###})");
            if (b.IsLinkChild != w.IsLinkChild) changes.Add($"link child {b.IsLinkChild} -> {w.IsLinkChild}");

            if (changes.Count > 0)
                lines.Add($"~ 0x{guid:X8} <{Names.WeenieRef(w.WeenieClassId)}>: {string.Join("; ", changes)}");
        }

        foreach (var (guid, w) in worldRows)
            if (!baseRows.ContainsKey(guid))
                lines.Add($"+ {Format.Instance(w)}");

        var baseLinks = LoadLinks(baseS, landblock);
        var worldLinks = LoadLinks(world, landblock);
        foreach (var l in baseLinks.Except(worldLinks)) lines.Add($"- link 0x{l.parent:X8} -> 0x{l.child:X8}");
        foreach (var l in worldLinks.Except(baseLinks)) lines.Add($"+ link 0x{l.parent:X8} -> 0x{l.child:X8}");

        return new Result(subject, baseRows.Count > 0, worldRows.Count > 0, lines);
    }

    private static Dictionary<uint, LandblockInstance> LoadInstances(string schema, ushort landblock) =>
        Db.Query($"SELECT * FROM `{schema}`.landblock_instance WHERE landblock = @lb", ("@lb", (int)landblock))
            .Select(r => new LandblockInstance
            {
                Guid = Convert.ToUInt32(r["guid"]),
                Landblock = landblock,
                WeenieClassId = Convert.ToUInt32(r["weenie_Class_Id"]),
                ObjCellId = Convert.ToUInt32(r["obj_Cell_Id"]),
                OriginX = Convert.ToSingle(r["origin_X"]),
                OriginY = Convert.ToSingle(r["origin_Y"]),
                OriginZ = Convert.ToSingle(r["origin_Z"]),
                AnglesW = Convert.ToSingle(r["angles_W"]),
                AnglesX = Convert.ToSingle(r["angles_X"]),
                AnglesY = Convert.ToSingle(r["angles_Y"]),
                AnglesZ = Convert.ToSingle(r["angles_Z"]),
                IsLinkChild = Convert.ToBoolean(r["is_Link_Child"]),
            })
            .ToDictionary(i => i.Guid, i => i);

    private static HashSet<(uint parent, uint child)> LoadLinks(string schema, ushort landblock) =>
        Db.Query($"SELECT l.parent_GUID, l.child_GUID FROM `{schema}`.landblock_instance_link l JOIN `{schema}`.landblock_instance i ON i.guid = l.parent_GUID WHERE i.landblock = @lb", ("@lb", (int)landblock))
            .Select(r => (Convert.ToUInt32(r["parent_GUID"]), Convert.ToUInt32(r["child_GUID"])))
            .ToHashSet();

    // ---- comparing and rendering ----------------------------------------------------------

    private static (List<Db.Row> removed, List<Db.Row> added) Compare(List<Db.Row> baseRows, List<Db.Row> worldRows, List<string> cols)
    {
        var baseKeyed = baseRows.Select(r => (key: Normalize(r, cols), row: r)).ToList();
        var worldKeyed = worldRows.Select(r => (key: Normalize(r, cols), row: r)).ToList();

        return (MultisetExcept(baseKeyed, worldKeyed, x => x.key).Select(x => x.row).ToList(),
                MultisetExcept(worldKeyed, baseKeyed, x => x.key).Select(x => x.row).ToList());
    }

    /// <summary>Elements of a whose key is not matched, one for one, by an element of b.</summary>
    private static List<TItem> MultisetExcept<TItem>(List<TItem> a, List<TItem> b, Func<TItem, string> keyOf)
    {
        var counts = new Dictionary<string, int>();
        foreach (var x in b)
            counts[keyOf(x)] = counts.GetValueOrDefault(keyOf(x)) + 1;

        var result = new List<TItem>();
        foreach (var x in a)
        {
            var key = keyOf(x);
            if (counts.GetValueOrDefault(key) > 0)
                counts[key]--;
            else
                result.Add(x);
        }
        return result;
    }

    /// <summary>A row as one string, column order fixed, floats rounded so 1.0 and 1.00001 agree.</summary>
    private static string Normalize(Db.Row row, List<string> cols)
    {
        var sb = new StringBuilder();
        foreach (var c in cols)
        {
            sb.Append(c).Append('=');
            var v = row[c];
            sb.Append(v switch
            {
                null => "NULL",
                float f => f.ToString("0.#####", Inv),
                double d => d.ToString("0.#####", Inv),
                bool b => b ? "1" : "0",
                byte[] bytes => Convert.ToHexString(bytes),
                _ => Convert.ToString(v, Inv),
            });
            sb.Append('');
        }
        return sb.ToString();
    }

    private static void RenderProperties(List<string> lines, string kind, List<Db.Row> removed, List<Db.Row> added)
    {
        var addedByType = added.ToDictionary(r => Convert.ToUInt32(r["type"]), r => r);

        foreach (var r in removed)
        {
            var type = Convert.ToUInt32(r["type"]);
            var name = Format.PropertyName(kind, type);

            if (addedByType.Remove(type, out var a))
                lines.Add($"~ {kind} {name}: {Format.PropertyValue(kind, type, r["value"])} -> {Format.PropertyValue(kind, type, a["value"])}");
            else
                lines.Add($"- {kind} {name} = {Format.PropertyValue(kind, type, r["value"])}");
        }

        foreach (var (type, a) in addedByType)
            lines.Add($"+ {kind} {Format.PropertyName(kind, type)} = {Format.PropertyValue(kind, type, a["value"])}");
    }

    private static void RenderRows(List<string> lines, string table, List<Db.Row> removed, List<Db.Row> added)
    {
        var label = table.Replace("weenie_properties_", "");

        foreach (var r in removed) lines.Add($"- {label} {Describe(table, r)}");
        foreach (var a in added) lines.Add($"+ {label} {Describe(table, a)}");
    }

    /// <summary>Rows of the tables /winfo knows how to read get its wording; the rest print their columns.</summary>
    private static string Describe(string table, Db.Row r)
    {
        try
        {
            switch (table)
            {
                case "weenie_properties_create_list":
                    return Format.CreateList(new WeeniePropertiesCreateList
                    {
                        DestinationType = Convert.ToSByte(r["destination_Type"]),
                        WeenieClassId = Convert.ToUInt32(r["weenie_Class_Id"]),
                        StackSize = Convert.ToInt32(r["stack_Size"]),
                        Palette = Convert.ToSByte(r["palette"]),
                        Shade = Convert.ToSingle(r["shade"]),
                        TryToBond = Convert.ToBoolean(r["try_To_Bond"]),
                    });

                case "weenie_properties_generator":
                    return Format.Generator(new WeeniePropertiesGenerator
                    {
                        Probability = Convert.ToSingle(r["probability"]),
                        WeenieClassId = Convert.ToUInt32(r["weenie_Class_Id"]),
                        Delay = F(r["delay"]),
                        InitCreate = Convert.ToInt32(r["init_Create"]),
                        MaxCreate = Convert.ToInt32(r["max_Create"]),
                        WhenCreate = Convert.ToUInt32(r["when_Create"]),
                        WhereCreate = Convert.ToUInt32(r["where_Create"]),
                        StackSize = I(r["stack_Size"]),
                        PaletteId = U(r["palette_Id"]),
                        Shade = F(r["shade"]),
                        ObjCellId = U(r["obj_Cell_Id"]),
                        OriginX = F(r["origin_X"]),
                        OriginY = F(r["origin_Y"]),
                        OriginZ = F(r["origin_Z"]),
                    });

                case "weenie_properties_spell_book":
                    return Format.SpellBook(new WeeniePropertiesSpellBook
                    {
                        Spell = Convert.ToInt32(r["spell"]),
                        Probability = Convert.ToSingle(r["probability"]),
                    });

                case "weenie_properties_skill":
                    return $"{Format.EnumName<Skill>(Convert.ToUInt32(r["type"]))} {r["init_Level"]}+{r["level_From_P_P"]} ({Format.EnumName<SkillAdvancementClass>(Convert.ToUInt32(r["s_a_c"]))})";

                case "weenie_properties_attribute":
                    return $"{Format.EnumName<PropertyAttribute>(Convert.ToUInt32(r["type"]))} {r["init_Level"]}+{r["level_From_C_P"]}";

                case "weenie_properties_attribute_2nd":
                    return $"{Format.EnumName<PropertyAttribute2nd>(Convert.ToUInt32(r["type"]))} {r["init_Level"]}+{r["level_From_C_P"]} cur {r["current_Level"]}";

                case "weenie_properties_position":
                    return $"{Format.EnumName<PositionType>(Convert.ToUInt32(r["position_Type"]))} 0x{Convert.ToUInt32(r["obj_Cell_Id"]):X8} ({Convert.ToSingle(r["origin_X"]):0.##}, {Convert.ToSingle(r["origin_Y"]):0.##}, {Convert.ToSingle(r["origin_Z"]):0.##})";
            }
        }
        catch
        {
            // A column this build does not know - fall through to the generic form.
        }

        return string.Join(" ", r.Select(kv => $"{kv.Key}={(kv.Value is null ? "NULL" : Convert.ToString(kv.Value, Inv))}"));
    }

    private static bool Near(float a, float b) => Math.Abs(a - b) < 0.0005f;

    private static uint? U(object? v) => v is null ? null : Convert.ToUInt32(v);
    private static int? I(object? v) => v is null ? null : Convert.ToInt32(v);
    private static long? L(object? v) => v is null ? null : Convert.ToInt64(v);
    private static float? F(object? v) => v is null ? null : Convert.ToSingle(v);
    private static double? D(object? v) => v is null ? null : Convert.ToDouble(v);
}
