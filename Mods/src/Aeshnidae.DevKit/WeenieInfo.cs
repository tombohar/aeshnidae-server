using ACE.Database.Models.World;

namespace Aeshnidae.DevKit;

/// <summary>
/// /winfo: everything a content developer wants to know about a weenie, as lines of
/// chat, with the interesting parts first. What it gives and awards, what it sells,
/// what it spawns, what it casts - decoded - then the raw properties, wrapped.
/// </summary>
internal static class WeenieInfo
{
    public static List<string> Render(WorldWeenie w, bool full)
    {
        var lines = new List<string>();

        var name = w.WeeniePropertiesString?.FirstOrDefault(s => s.Type == (ushort)PropertyString.Name)?.Value ?? "(no name)";
        var level = w.WeeniePropertiesInt?.FirstOrDefault(i => i.Type == (ushort)PropertyInt.Level)?.Value;
        var itemType = w.WeeniePropertiesInt?.FirstOrDefault(i => i.Type == (ushort)PropertyInt.ItemType)?.Value;

        lines.Add($"=== {w.ClassId} {name}  ({w.ClassName})  {Format.EnumName<WeenieType>((uint)w.Type)}" +
                  (itemType is not null ? $" / {Format.EnumName<ItemType>((uint)itemType.Value)}" : "") +
                  (level is not null ? $"  level {level}" : "") +
                  $"  modified {w.LastModified:yyyy-MM-dd}");

        // The parts people actually edit, decoded.
        var emotes = w.WeeniePropertiesEmote?.OrderBy(e => e.Category).ThenBy(e => e.WeenieClassId).ThenBy(e => e.Id).ToList();
        if (emotes is { Count: > 0 })
        {
            lines.Add($"-- Emotes ({emotes.Count}):");
            foreach (var e in emotes)
                lines.Add("  " + Format.Emote(e));
        }

        var create = w.WeeniePropertiesCreateList?.OrderBy(c => c.DestinationType).ThenBy(c => c.Id).ToList();
        if (create is { Count: > 0 })
        {
            var shop = create.Where(c => (c.DestinationType & (sbyte)DestinationType.Shop) != 0).ToList();
            var rest = create.Except(shop).ToList();
            if (shop.Count > 0)
            {
                lines.Add($"-- Sells ({shop.Count}):");
                lines.AddRange(Format.Wrap("  ", shop.Select(c => $"<{Names.WeenieRef(c.WeenieClassId)}>" + (c.StackSize is > 1 or < 0 ? $" x{c.StackSize}" : ""))));
            }
            if (rest.Count > 0)
            {
                lines.Add($"-- Create list ({rest.Count}):");
                lines.AddRange(Format.Wrap("  ", rest.Select(Format.CreateList)));
            }
        }

        var gen = w.WeeniePropertiesGenerator?.OrderBy(g => g.Id).ToList();
        if (gen is { Count: > 0 })
        {
            lines.Add($"-- Generates ({gen.Count}):");
            foreach (var g in gen)
                lines.Add("  " + Format.Generator(g));
        }

        var spells = w.WeeniePropertiesSpellBook?.OrderBy(s => s.Id).ToList();
        if (spells is { Count: > 0 })
        {
            lines.Add($"-- Spells ({spells.Count}):");
            lines.AddRange(Format.Wrap("  ", spells.Select(Format.SpellBook)));
        }

        if (w.WeeniePropertiesAttribute is { Count: > 0 })
            lines.AddRange(Format.Wrap("-- Attributes: ", w.WeeniePropertiesAttribute.OrderBy(a => a.Type)
                .Select(a => $"{Format.EnumName<PropertyAttribute>(a.Type)} {a.InitLevel}" + (a.LevelFromCP > 0 ? $"+{a.LevelFromCP}" : ""))));

        if (w.WeeniePropertiesAttribute2nd is { Count: > 0 })
            lines.AddRange(Format.Wrap("-- Vitals: ", w.WeeniePropertiesAttribute2nd.OrderBy(a => a.Type)
                .Select(a => $"{Format.EnumName<PropertyAttribute2nd>(a.Type)} {a.InitLevel}" + (a.LevelFromCP > 0 ? $"+{a.LevelFromCP}" : "") + (a.CurrentLevel > 0 ? $" cur {a.CurrentLevel}" : ""))));

        if (w.WeeniePropertiesSkill is { Count: > 0 })
            lines.AddRange(Format.Wrap("-- Skills: ", w.WeeniePropertiesSkill.OrderBy(s => s.Type)
                .Select(s => $"{Format.EnumName<Skill>(s.Type)} {s.InitLevel}" + (s.LevelFromPP > 0 ? $"+{s.LevelFromPP}" : "") + $" ({Format.EnumName<SkillAdvancementClass>(s.SAC)})")));

        if (w.WeeniePropertiesBodyPart is { Count: > 0 })
            lines.Add($"-- Body parts: {w.WeeniePropertiesBodyPart.Count} (see /wexport for the table)");

        if (w.WeeniePropertiesPosition is { Count: > 0 })
            lines.AddRange(Format.Wrap("-- Positions: ", w.WeeniePropertiesPosition.OrderBy(p => p.PositionType)
                .Select(p => $"{Format.EnumName<PositionType>(p.PositionType)} 0x{p.ObjCellId:X8} ({p.OriginX:0.##}, {p.OriginY:0.##}, {p.OriginZ:0.##})")));

        if (w.WeeniePropertiesEventFilter is { Count: > 0 })
            lines.AddRange(Format.Wrap("-- Event filters: ", w.WeeniePropertiesEventFilter.Select(f => f.Event.ToString())));

        if (w.WeeniePropertiesBook is not null)
            lines.Add($"-- Book: {w.WeeniePropertiesBook.MaxNumPages} pages max, {w.WeeniePropertiesBookPageData?.Count ?? 0} written");

        // Raw properties. Strings first (name, descriptions), then the numbers.
        Props(lines, "string", w.WeeniePropertiesString?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "int", w.WeeniePropertiesInt?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "int64", w.WeeniePropertiesInt64?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "bool", w.WeeniePropertiesBool?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "float", w.WeeniePropertiesFloat?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "d_i_d", w.WeeniePropertiesDID?.Select(p => ((uint)p.Type, (object?)p.Value)));
        Props(lines, "i_i_d", w.WeeniePropertiesIID?.Select(p => ((uint)p.Type, (object?)p.Value)));

        if (full)
        {
            if (w.WeeniePropertiesPalette is { Count: > 0 })
                lines.AddRange(Format.Wrap("-- Palettes: ", w.WeeniePropertiesPalette.Select(p => $"0x{p.SubPaletteId:X} [{p.Offset}+{p.Length}]")));
            if (w.WeeniePropertiesTextureMap is { Count: > 0 })
                lines.AddRange(Format.Wrap("-- Textures: ", w.WeeniePropertiesTextureMap.Select(t => $"{t.Index}: 0x{t.OldId:X}->0x{t.NewId:X}")));
            if (w.WeeniePropertiesAnimPart is { Count: > 0 })
                lines.AddRange(Format.Wrap("-- Anim parts: ", w.WeeniePropertiesAnimPart.Select(a => $"{a.Index}: 0x{a.AnimationId:X}")));
        }
        else
        {
            var visual = (w.WeeniePropertiesPalette?.Count ?? 0) + (w.WeeniePropertiesTextureMap?.Count ?? 0) + (w.WeeniePropertiesAnimPart?.Count ?? 0);
            if (visual > 0)
                lines.Add($"-- {visual} palette/texture/anim rows hidden - /winfo {w.ClassId} full");
        }

        return lines;
    }

    private static void Props(List<string> lines, string kind, IEnumerable<(uint type, object? value)>? props)
    {
        if (props is null) return;
        var list = props.OrderBy(p => p.type).ToList();
        if (list.Count == 0) return;

        lines.AddRange(Format.Wrap($"-- {kind}: ", list.Select(p => $"{Format.PropertyName(kind, p.type)}={Format.PropertyValue(kind, p.type, p.value)}")));
    }
}
