using ACE.Database.Models.World;
using System.Globalization;

namespace Aeshnidae.DevKit;

/// <summary>
/// One-line renderings of the parts of a weenie a content developer asks about, in the
/// terms they think in: "Give tusk -> AwardXP 95,000,000; Tell ..." rather than a row
/// in weenie_properties_emote_action with type = 2 and amount_64 = 95000000.
///
/// Used by /winfo to describe a weenie and by /wdiff to describe what changed, so the
/// two read the same.
/// </summary>
internal static class Format
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // ---- emotes --------------------------------------------------------------

    /// <summary>The emote's trigger: category, what item or text triggers it, its odds.</summary>
    public static string EmoteHeader(WeeniePropertiesEmote e)
    {
        var sb = new StringBuilder();
        sb.Append(EnumName<EmoteCategory>(e.Category));

        if (e.WeenieClassId is not null)
            sb.Append(' ').Append('<').Append(Names.WeenieRef(e.WeenieClassId)).Append('>');

        if (!string.IsNullOrEmpty(e.Quest))
            sb.Append(" \"").Append(e.Quest).Append('"');

        if (e.VendorType is not null)
            sb.Append(" vendorType=").Append(e.VendorType);

        if (e.Style is not null || e.Substyle is not null)
            sb.Append(" style=").Append(Hex(e.Style)).Append('/').Append(Hex(e.Substyle));

        if (e.MinHealth is not null || e.MaxHealth is not null)
            sb.Append(" health=").Append(e.MinHealth?.ToString(Inv) ?? "").Append("..").Append(e.MaxHealth?.ToString(Inv) ?? "");

        if (Math.Abs(e.Probability - 1f) > 0.0001f)
            sb.Append(" p=").Append(e.Probability.ToString("0.###", Inv));

        return sb.ToString();
    }

    /// <summary>Trigger plus every action, on one line.</summary>
    public static string Emote(WeeniePropertiesEmote e)
    {
        var actions = e.WeeniePropertiesEmoteAction?.OrderBy(a => a.Order).Select(Action) ?? Enumerable.Empty<string>();
        return $"{EmoteHeader(e)}: {string.Join(" ; ", actions)}";
    }

    public static string Action(WeeniePropertiesEmoteAction a)
    {
        var type = (EmoteType)a.Type;
        var name = Enum.IsDefined(type) ? type.ToString() : $"type{a.Type}";

        switch (type)
        {
            case EmoteType.AwardXP:
            case EmoteType.AwardNoShareXP:
            case EmoteType.AwardLuminance:
            case EmoteType.SpendLuminance:
                return $"{name} {N(a.Amount64 ?? a.Amount)}" + (a.HeroXP64 is > 0 ? $" (hero {N(a.HeroXP64)})" : "");

            case EmoteType.AwardLevelProportionalXP:
                return $"{name} {Pct(a.Percent)} of level, {N(a.Min64)}..{N(a.Max64)}" + (a.Display == true ? " (shown)" : "");

            case EmoteType.AwardSkillXP:
            case EmoteType.AwardSkillPoints:
                return $"{name} {SkillName(a.Stat)} {N(a.Amount)}";

            case EmoteType.Tell:
            case EmoteType.Say:
            case EmoteType.TextDirect:
            case EmoteType.DirectBroadcast:
            case EmoteType.LocalBroadcast:
            case EmoteType.WorldBroadcast:
            case EmoteType.TellFellow:
            case EmoteType.PopUp:
                return $"{name} \"{Clip(a.Message, 90)}\"";

            case EmoteType.Give:
            case EmoteType.TakeItems:
                return $"{name} {(a.StackSize is > 1 ? a.StackSize + "x " : "")}<{Names.WeenieRef(a.WeenieClassId)}>";

            case EmoteType.CastSpell:
            case EmoteType.CastSpellInstant:
            case EmoteType.PetCastSpellOnOwner:
                return $"{name} {Names.Spell(a.SpellId)}";

            case EmoteType.CreateTreasure:
                return $"{name} wealth={a.WealthRating} class={a.TreasureClass} type={a.TreasureType}";

            case EmoteType.Motion:
            case EmoteType.ForceMotion:
                return $"{name} 0x{a.Motion:X}";

            case EmoteType.Goto:
                return $"{name} \"{a.Message}\"";

            case EmoteType.Sound:
                return $"{name} {a.Sound}";

            case EmoteType.PhysScript:
                return $"{name} {a.PScript}";
        }

        // Quest actions carry the quest name in Message; stat inquiries carry a stat
        // and a range. Anything else prints whichever fields it has set.
        var sb = new StringBuilder(name);

        if (!string.IsNullOrEmpty(a.Message))
            sb.Append(name.StartsWith("Inq") || name.Contains("Quest") || name.Contains("Event") ? " " : " \"").Append(Clip(a.Message, 60)).Append(name.StartsWith("Inq") || name.Contains("Quest") || name.Contains("Event") ? "" : "\"");

        if (a.Stat is not null)
            sb.Append(" stat=").Append(StatName(type, a.Stat.Value));
        if (a.Min is not null || a.Max is not null)
            sb.Append(' ').Append(a.Min?.ToString(Inv) ?? "").Append("..").Append(a.Max?.ToString(Inv) ?? "");
        if (a.Min64 is not null || a.Max64 is not null)
            sb.Append(' ').Append(N(a.Min64)).Append("..").Append(N(a.Max64));
        if (a.MinDbl is not null || a.MaxDbl is not null)
            sb.Append(' ').Append(a.MinDbl?.ToString("0.###", Inv) ?? "").Append("..").Append(a.MaxDbl?.ToString("0.###", Inv) ?? "");
        if (a.Amount is not null)
            sb.Append(" amount=").Append(N(a.Amount));
        if (a.Amount64 is not null)
            sb.Append(" amount64=").Append(N(a.Amount64));
        if (a.Percent is not null)
            sb.Append(' ').Append(Pct(a.Percent));
        if (a.WeenieClassId is not null)
            sb.Append(" <").Append(Names.WeenieRef(a.WeenieClassId)).Append('>');
        if (a.SpellId is not null)
            sb.Append(' ').Append(Names.Spell(a.SpellId));
        if (!string.IsNullOrEmpty(a.TestString))
            sb.Append(" test=\"").Append(Clip(a.TestString, 40)).Append('"');
        if (a.Delay > 0)
            sb.Append(" delay=").Append(a.Delay.ToString("0.##", Inv));

        return sb.ToString();
    }

    // ---- other tables ----------------------------------------------------------

    public static string CreateList(WeeniePropertiesCreateList c)
    {
        var dest = EnumName<DestinationType>((uint)c.DestinationType);
        var qty = c.StackSize is > 1 or < 0 ? $"{c.StackSize}x " : "";
        var extra = "";
        if (c.Palette != 0) extra += $" palette={c.Palette}";
        if (c.Shade != 0) extra += $" shade={c.Shade.ToString("0.##", Inv)}";
        if (c.TryToBond) extra += " bond";
        return $"{dest} {qty}<{Names.WeenieRef(c.WeenieClassId)}>{extra}";
    }

    public static string Generator(WeeniePropertiesGenerator g)
    {
        var where = EnumName<RegenLocationType>(g.WhereCreate);
        var when = EnumName<RegenerationType>(g.WhenCreate);
        var s = $"<{Names.WeenieRef(g.WeenieClassId)}> init={g.InitCreate} max={g.MaxCreate} p={g.Probability.ToString("0.###", Inv)} {when}/{where}";
        if (g.Delay is not null) s += $" delay={g.Delay.Value.ToString("0.#", Inv)}s";
        if (g.StackSize is not null) s += $" stack={g.StackSize}";
        if (g.OriginX is not null) s += $" @({g.OriginX?.ToString("0.##", Inv)}, {g.OriginY?.ToString("0.##", Inv)}, {g.OriginZ?.ToString("0.##", Inv)})";
        return s;
    }

    public static string SpellBook(WeeniePropertiesSpellBook s) =>
        Math.Abs(s.Probability - 2f) < 0.0001f || s.Probability >= 1f
            ? Names.Spell(s.Spell)
            : $"{Names.Spell(s.Spell)} p={s.Probability.ToString("0.###", Inv)}";

    public static string Instance(LandblockInstance i) =>
        $"0x{i.Guid:X8} <{Names.WeenieRef(i.WeenieClassId)}> cell 0x{i.ObjCellId:X8} @({i.OriginX.ToString("0.###", Inv)}, {i.OriginY.ToString("0.###", Inv)}, {i.OriginZ.ToString("0.###", Inv)})" +
        $" rot({i.AnglesW.ToString("0.###", Inv)}, {i.AnglesX.ToString("0.###", Inv)}, {i.AnglesY.ToString("0.###", Inv)}, {i.AnglesZ.ToString("0.###", Inv)})" +
        (i.IsLinkChild ? " child" : "");

    /// <summary>"Level" for (int, 25); "Name" for (string, 1) - the property's enum name.</summary>
    public static string PropertyName(string kind, uint type) => kind switch
    {
        "int"    => EnumName<PropertyInt>(type),
        "int64"  => EnumName<PropertyInt64>(type),
        "bool"   => EnumName<PropertyBool>(type),
        "float"  => EnumName<PropertyFloat>(type),
        "string" => EnumName<PropertyString>(type),
        "d_i_d"  => EnumName<PropertyDataId>(type),
        "i_i_d"  => EnumName<PropertyInstanceId>(type),
        "position" => EnumName<PositionType>(type),
        "attribute" => EnumName<PropertyAttribute>(type),
        "attribute_2nd" => EnumName<PropertyAttribute2nd>(type),
        "skill" => EnumName<Skill>(type),
        _ => type.ToString(),
    };

    /// <summary>Value rendered for its property: enums decoded where ACE has one, ids in hex.</summary>
    public static string PropertyValue(string kind, uint type, object? value)
    {
        if (value is null) return "null";

        if (kind == "int")
        {
            var v = Convert.ToInt32(value);
            return (PropertyInt)type switch
            {
                PropertyInt.ItemType => $"{EnumName<ItemType>((uint)v)} ({v})",
                PropertyInt.CreatureType => $"{EnumName<CreatureType>((uint)v)} ({v})",
                PropertyInt.PlayerKillerStatus => $"{EnumName<PlayerKillerStatus>((uint)v)} ({v})",
                PropertyInt.RadarBlipColor => $"{EnumName<RadarColor>((uint)v)} ({v})",
                PropertyInt.ItemUseable => $"{EnumName<Usable>((uint)v)} ({v})",
                PropertyInt.MerchandiseItemTypes => $"0x{v:X}",
                PropertyInt.PortalBitmask => $"{EnumName<PortalBitmask>((uint)v)} ({v})",
                PropertyInt.AmmoType => $"{EnumName<AmmoType>((uint)v)} ({v})",
                PropertyInt.DamageType => $"{EnumName<DamageType>((uint)v)} ({v})",
                PropertyInt.CombatUse => $"{EnumName<CombatUse>((uint)v)} ({v})",
                PropertyInt.ValidLocations or PropertyInt.CurrentWieldedLocation => $"{EnumName<EquipMask>((uint)v)} ({v})",
                PropertyInt.WieldSkillType => $"{EnumName<Skill>((uint)v)} ({v})",
                PropertyInt.WieldRequirements => $"{EnumName<WieldRequirement>((uint)v)} ({v})",
                _ => N(v),
            };
        }

        if (kind == "int64") return N(Convert.ToInt64(value));
        if (kind == "d_i_d" || kind == "i_i_d") return $"0x{Convert.ToUInt32(value):X8}";
        if (kind == "bool") return Convert.ToBoolean(value) ? "True" : "False";
        if (kind == "float") return Convert.ToDouble(value).ToString("0.####", Inv);
        if (kind == "string") return $"\"{value}\"";

        return Convert.ToString(value, Inv) ?? "";
    }

    // ---- helpers ----------------------------------------------------------------

    public static string EnumName<T>(uint value) where T : struct, Enum
    {
        var t = (T)Enum.ToObject(typeof(T), value);
        return Enum.IsDefined(t) || typeof(T).IsDefined(typeof(FlagsAttribute), false) ? t.ToString() : $"{typeof(T).Name}{value}";
    }

    private static string SkillName(int? stat) => stat is null ? "" : EnumName<Skill>((uint)stat.Value);

    private static string StatName(EmoteType type, int stat)
    {
        var n = type.ToString();
        if (n.Contains("Int64")) return EnumName<PropertyInt64>((uint)stat);
        if (n.Contains("Int")) return EnumName<PropertyInt>((uint)stat);
        if (n.Contains("Bool")) return EnumName<PropertyBool>((uint)stat);
        if (n.Contains("Float")) return EnumName<PropertyFloat>((uint)stat);
        if (n.Contains("String")) return EnumName<PropertyString>((uint)stat);
        if (n.Contains("Skill")) return EnumName<Skill>((uint)stat);
        if (n.Contains("SecondaryAttribute")) return EnumName<PropertyAttribute2nd>((uint)stat);
        if (n.Contains("Attribute")) return EnumName<PropertyAttribute>((uint)stat);
        return stat.ToString(Inv);
    }

    public static string N(long? v) => v is null ? "" : v.Value.ToString("#,0", Inv);
    public static string N(int? v) => v is null ? "" : v.Value.ToString("#,0", Inv);
    private static string Pct(double? p) => p is null ? "" : (p.Value * 100).ToString("0.##", Inv) + "%";
    private static string Hex(uint? v) => v is null ? "" : $"0x{v:X}";

    public static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", "").Replace("\n", " / ");
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>Wrap "a, b, c, ..." lines at roughly the width the chat window shows without scrolling.</summary>
    public static IEnumerable<string> Wrap(string prefix, IEnumerable<string> items, int width = 170)
    {
        var line = new StringBuilder(prefix);
        var first = true;

        foreach (var item in items)
        {
            if (!first && line.Length + item.Length + 2 > width)
            {
                yield return line.ToString();
                line.Clear().Append(new string(' ', Math.Min(prefix.Length, 4)));
                first = true;
            }

            if (!first) line.Append(", ");
            line.Append(item);
            first = false;
        }

        if (line.Length > 0 && !first)
            yield return line.ToString();
    }
}
