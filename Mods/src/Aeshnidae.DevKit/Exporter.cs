using ACE.Database.SQLFormatters.World;

namespace Aeshnidae.DevKit;

/// <summary>
/// Renders a weenie, or a landblock's instances, to SQL the same way ACE's own
/// /export-sql and /createinst do - but into memory, so the bytes can go to Discord,
/// into a submission, or anywhere else, without touching the content folder.
///
/// The writers are built once and kept. WeenieSQLWriter's constructor pulls every
/// weenie name and spell name from the database to label its output with comments,
/// which is the slow part of ACE's exporter too; here it is paid on the first export
/// only. (Ported from Aeshnidae.ContentTools, which this mod supersedes.)
/// </summary>
internal static class Exporter
{
    private static WeenieSQLWriter? _weenieWriter;
    private static LandblockInstanceWriter? _landblockWriter;
    private static readonly object _lock = new();

    /// <summary>UTF-8 without a BOM - a BOM in front of "DELETE FROM" is a syntax error to mariadb.</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public sealed record Result(WorldWeenie Weenie, string FileName, byte[] Sql, int Lines);

    public sealed record LandblockResult(ushort Landblock, string FileName, byte[] Sql, int Instances, int Lines);

    private static WeenieSQLWriter WeenieWriter
    {
        get
        {
            if (_weenieWriter is not null) return _weenieWriter;

            lock (_lock)
            {
                _weenieWriter ??= new WeenieSQLWriter
                {
                    WeenieNames     = Names.Weenies,
                    SpellNames      = Names.Spells,
                    TreasureDeath   = DatabaseManager.World.GetAllTreasureDeath(),
                    TreasureWielded = DatabaseManager.World.GetAllTreasureWielded(),
                    PacketOpCodes   = PacketOpCodeNames.Values,
                };
            }

            return _weenieWriter;
        }
    }

    private static LandblockInstanceWriter LandblockWriter
    {
        get
        {
            if (_landblockWriter is not null) return _landblockWriter;
            lock (_lock) _landblockWriter ??= new LandblockInstanceWriter { WeenieNames = Names.Weenies };
            return _landblockWriter;
        }
    }

    /// <summary>Accepts a wcid or a class name, exactly as /export-sql does.</summary>
    public static WorldWeenie? Find(string wcidOrName) =>
        uint.TryParse(wcidOrName, out var wcid)
            ? DatabaseManager.World.GetWeenie(wcid)
            : DatabaseManager.World.GetWeenie(wcidOrName);

    public static Result? Export(string wcidOrName)
    {
        var weenie = Find(wcidOrName);
        if (weenie is null)
            return null;

        var writer = WeenieWriter;

        using var stream = new MemoryStream();
        using (var sw = new StreamWriter(stream, Utf8NoBom, leaveOpen: true))
        {
            writer.CreateSQLDELETEStatement(weenie, sw);
            sw.WriteLine();
            writer.CreateSQLINSERTStatement(weenie, sw);
        }

        var bytes = stream.ToArray();
        return new Result(weenie, writer.GetDefaultFileName(weenie), bytes, CountLines(bytes));
    }

    /// <summary>
    /// The landblock's instances as ACE's own DELETE + INSERT block, read fresh from
    /// the database (the cache is dropped first so a /createinst a moment ago is in it).
    /// Null when the landblock has no instances at all.
    /// </summary>
    public static LandblockResult? ExportLandblock(ushort landblock)
    {
        DatabaseManager.World.ClearCachedInstancesByLandblock(landblock);
        var instances = DatabaseManager.World.GetCachedInstancesByLandblock(landblock);

        if (instances is null || instances.Count == 0)
            return null;

        var writer = LandblockWriter;

        using var stream = new MemoryStream();
        using (var sw = new StreamWriter(stream, Utf8NoBom, leaveOpen: true))
        {
            writer.CreateSQLDELETEStatement(instances, sw);
            sw.WriteLine();
            writer.CreateSQLINSERTStatement(instances, sw);
        }

        var bytes = stream.ToArray();
        return new LandblockResult(landblock, $"{landblock:X4}.sql", bytes, instances.Count, CountLines(bytes));
    }

    private static int CountLines(byte[] bytes)
    {
        var lines = 0;
        foreach (var b in bytes) if (b == (byte)'\n') lines++;
        return lines;
    }

    /// <summary>Drop the cached writers so a reload picks up renamed weenies or spells.</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _weenieWriter = null;
            _landblockWriter = null;
        }
    }
}
