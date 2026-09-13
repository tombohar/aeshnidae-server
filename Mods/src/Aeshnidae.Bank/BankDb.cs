namespace Aeshnidae.Bank;

/// <summary>
/// Balances live in their own table in the shard database, keyed by account id so
/// every character on an account shares one balance.
///
/// The shard db is used rather than a side file so balances are covered by the
/// same backups as the characters they belong to. Connection details come from
/// ACE's own Config.js at runtime - no credentials in the mod.
/// </summary>
public static class BankDb
{
    public const string TableName = "aeshnidae_bank";

    private static string _connectionString = "";

    private static readonly object _lock = new();

    public static bool Ready { get; private set; }

    public static void Initialize()
    {
        var cfg = ConfigManager.Config?.MySql?.Shard
                  ?? throw new InvalidOperationException("shard database is not configured");

        _connectionString = new MySqlConnectionStringBuilder
        {
            Server = cfg.Host,
            Port = cfg.Port,
            Database = cfg.Database,
            UserID = cfg.Username,
            Password = cfg.Password,
        }.ConnectionString;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            CREATE TABLE IF NOT EXISTS `{TableName}` (
                `account_Id` INT UNSIGNED NOT NULL,
                `currency`   VARCHAR(32)  NOT NULL,
                `amount`     BIGINT       NOT NULL DEFAULT 0,
                `updated`    DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
                                          ON UPDATE CURRENT_TIMESTAMP,
                PRIMARY KEY (`account_Id`, `currency`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        cmd.ExecuteNonQuery();

        // Per-CHARACTER switches, unlike balances which are per account. Auto-banking
        // luminance is a choice about how one character plays - a mule that should hoard
        // and a main that should bank are the same account - so it cannot live beside
        // the balance it feeds.
        using var flags = conn.CreateCommand();
        flags.CommandText = $@"
            CREATE TABLE IF NOT EXISTS `{FlagsTableName}` (
                `character_Id` INT UNSIGNED NOT NULL,
                `flag`         VARCHAR(32)  NOT NULL,
                `enabled`      TINYINT(1)   NOT NULL DEFAULT 0,
                PRIMARY KEY (`character_Id`, `flag`)
            ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
        flags.ExecuteNonQuery();

        Ready = true;
    }

    public const string FlagsTableName = "aeshnidae_bank_flags";

    public static bool GetFlag(uint characterId, string flag)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT `enabled` FROM `{FlagsTableName}` WHERE `character_Id` = @c AND `flag` = @f;";
        cmd.Parameters.AddWithValue("@c", characterId);
        cmd.Parameters.AddWithValue("@f", flag);

        var result = cmd.ExecuteScalar();
        return result is not (null or DBNull) && Convert.ToInt32(result) != 0;
    }

    public static void SetFlag(uint characterId, string flag, bool enabled)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            INSERT INTO `{FlagsTableName}` (`character_Id`, `flag`, `enabled`) VALUES (@c, @f, @e)
            ON DUPLICATE KEY UPDATE `enabled` = @e;";
        cmd.Parameters.AddWithValue("@c", characterId);
        cmd.Parameters.AddWithValue("@f", flag);
        cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    private static MySqlConnection Open()
    {
        var conn = new MySqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    public static long GetBalance(uint accountId, CurrencyKind kind)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT `amount` FROM `{TableName}` WHERE `account_Id` = @a AND `currency` = @c;";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@c", kind.ToString());

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    public static Dictionary<CurrencyKind, long> GetAllBalances(uint accountId)
    {
        var balances = Enum.GetValues<CurrencyKind>().ToDictionary(k => k, _ => 0L);

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT `currency`, `amount` FROM `{TableName}` WHERE `account_Id` = @a;";
        cmd.Parameters.AddWithValue("@a", accountId);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (Enum.TryParse<CurrencyKind>(reader.GetString(0), out var kind))
                balances[kind] = reader.GetInt64(1);
        }

        return balances;
    }

    /// <summary>
    /// Applies a signed change and returns the new balance, refusing to go negative.
    ///
    /// The UPDATE carries its own `amount + @d >= 0` guard so two characters on the
    /// same account withdrawing at once cannot drive the balance below zero - the
    /// second one matches no rows and reports failure.
    /// </summary>
    public static bool TryAdjust(uint accountId, CurrencyKind kind, long delta, out long newBalance)
    {
        lock (_lock)
        {
            using var conn = Open();
            using var tx = conn.BeginTransaction();

            using (var insert = conn.CreateCommand())
            {
                insert.Transaction = tx;
                insert.CommandText =
                    $"INSERT IGNORE INTO `{TableName}` (`account_Id`, `currency`, `amount`) VALUES (@a, @c, 0);";
                insert.Parameters.AddWithValue("@a", accountId);
                insert.Parameters.AddWithValue("@c", kind.ToString());
                insert.ExecuteNonQuery();
            }

            int rows;
            using (var update = conn.CreateCommand())
            {
                update.Transaction = tx;
                update.CommandText = $@"
                    UPDATE `{TableName}` SET `amount` = `amount` + @d
                    WHERE `account_Id` = @a AND `currency` = @c AND `amount` + @d >= 0;";
                update.Parameters.AddWithValue("@a", accountId);
                update.Parameters.AddWithValue("@c", kind.ToString());
                update.Parameters.AddWithValue("@d", delta);
                rows = update.ExecuteNonQuery();
            }

            long balance;
            using (var select = conn.CreateCommand())
            {
                select.Transaction = tx;
                select.CommandText =
                    $"SELECT `amount` FROM `{TableName}` WHERE `account_Id` = @a AND `currency` = @c;";
                select.Parameters.AddWithValue("@a", accountId);
                select.Parameters.AddWithValue("@c", kind.ToString());
                balance = Convert.ToInt64(select.ExecuteScalar() ?? 0L);
            }

            tx.Commit();

            newBalance = balance;
            return rows > 0;
        }
    }
}
