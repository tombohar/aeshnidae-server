namespace Aeshnidae.DevKit;

/// <summary>
/// A raw connection to the world database server, for the one thing ACE's own data
/// layer cannot do: read a SECOND schema on the same server (the base copy) and
/// compare it to the world. Connection details come from ACE's Config.js at runtime -
/// no credentials in the mod, and the same user as the server, so if ACE can read it,
/// this can.
///
/// Everything here is read-only. The only write path in the mod goes through ACE's
/// own importer (see Commands.HandleImport).
/// </summary>
public static class Db
{
    private static string _connectionString = "";

    public static bool Ready { get; private set; }

    /// <summary>Name of the schema ACE runs the world from, e.g. aeshnidae_world.</summary>
    public static string WorldSchema { get; private set; } = "";

    public static void Initialize()
    {
        var cfg = ConfigManager.Config?.MySql?.World
                  ?? throw new InvalidOperationException("world database is not configured");

        WorldSchema = cfg.Database;

        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = cfg.Host,
            Port = cfg.Port,
            Database = cfg.Database,
            UserID = cfg.Username,
            Password = cfg.Password,
            ConnectionTimeout = 10,
        }.ConnectionString;

        using var conn = Open();
        Ready = true;
    }

    private static MySqlConnection Open()
    {
        var conn = new MySqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    /// <summary>A row as column name to value; DBNull becomes null.</summary>
    public sealed class Row : Dictionary<string, object?>
    {
        public Row() : base(StringComparer.OrdinalIgnoreCase) { }
    }

    public static List<Row> Query(string sql, params (string name, object? value)[] args)
    {
        var rows = new List<Row>();

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Row();
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    public static object? Scalar(string sql, params (string name, object? value)[] args)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static bool SchemaExists(string schema) =>
        Scalar("SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name = @s", ("@s", schema)) is long n && n > 0;

    /// <summary>Column names of a table, in ordinal order, minus the ones passed to skip.</summary>
    public static List<string> Columns(string schema, string table, params string[] skip)
    {
        var rows = Query(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @s AND table_name = @t ORDER BY ordinal_position",
            ("@s", schema), ("@t", table));

        var skipSet = new HashSet<string>(skip, StringComparer.OrdinalIgnoreCase);
        return rows.Select(r => (string)r["column_name"]!).Where(c => !skipSet.Contains(c)).ToList();
    }

    /// <summary>
    /// Schema names are interpolated into SQL (they cannot be parameters), so they are
    /// checked against the only shape MariaDB identifiers take here.
    /// </summary>
    public static string SafeIdent(string ident)
    {
        if (string.IsNullOrEmpty(ident) || ident.Length > 64 || !ident.All(c => char.IsLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"'{ident}' is not a plain identifier");
        return ident;
    }
}

/// <summary>
/// Weenie and spell names for labelling output. Pulled once from ACE's own lookups
/// (a full scan of the string table, the same one /export-sql pays for) and kept
/// until the mod is reloaded.
/// </summary>
public static class Names
{
    private static Dictionary<uint, string>? _weenies;
    private static Dictionary<uint, string>? _spells;
    private static readonly object _lock = new();

    public static Dictionary<uint, string> Weenies
    {
        get
        {
            if (_weenies is not null) return _weenies;
            lock (_lock) _weenies ??= DatabaseManager.World.GetAllWeenieNames();
            return _weenies;
        }
    }

    public static Dictionary<uint, string> Spells
    {
        get
        {
            if (_spells is not null) return _spells;
            lock (_lock) _spells ??= DatabaseManager.World.GetAllSpellNames();
            return _spells;
        }
    }

    public static string Weenie(uint? wcid) =>
        wcid is null ? "" : Weenies.TryGetValue(wcid.Value, out var n) ? n : $"wcid {wcid}";

    public static string WeenieRef(uint? wcid) =>
        wcid is null ? "" : $"{wcid} {Weenie(wcid)}";

    public static string Spell(int? id) =>
        id is null ? "" : Spells.TryGetValue((uint)id.Value, out var n) ? $"{n} ({id})" : $"spell {id}";

    public static void Reset()
    {
        lock (_lock)
        {
            _weenies = null;
            _spells = null;
        }
    }
}
