namespace Aeshnidae.InstancesNoDat;

/// <summary>
/// Who shares a copy.
///
/// This is the whole difference between a solo dungeon and a guild hall, and it is one
/// value per dungeon rather than a different mechanism for each - a copy is keyed on a
/// group, and Personal simply means the group is one person.
/// </summary>
public enum InstanceScope
{
    /// <summary>One copy per character.</summary>
    Personal,

    /// <summary>One copy per fellowship, so a party stays together. Solo players get their own.</summary>
    Fellowship,

    /// <summary>One copy per allegiance, shared by every member. Unsworn players get their own.</summary>
    Allegiance,
}

/// <summary>A dungeon that hands out copies, and who shares them.</summary>
public class InstancedLandblock
{
    /// <summary>Four-digit hex landblock id, as it appears in the cell dat.</summary>
    public string Landblock { get; set; } = "";

    public InstanceScope Scope { get; set; } = InstanceScope.Personal;

    /// <summary>
    /// Optional label used in messages, so players are told "your allegiance hall"
    /// rather than "copy 3 of 8602".
    /// </summary>
    public string Name { get; set; } = "";

    public bool TryParse(out ushort landblock)
    {
        var text = (Landblock ?? "").Trim().Replace("0x", "", StringComparison.OrdinalIgnoreCase);

        return ushort.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out landblock);
    }
}
