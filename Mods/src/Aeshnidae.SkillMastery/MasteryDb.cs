namespace Aeshnidae.SkillMastery;

/// <summary>
/// Mastery ranks live in their own table in the shard database, keyed by character.
///
/// Deliberately nowhere near the skill itself. ResetSkill zeroes Ranks, InitLevel
/// and ExperienceSpent, enlightenment runs every skill through it, and Train,
/// Specialize, Untrain and Unspecialize all bare-assign InitLevel afterwards.
/// Anything kept inside the skill would be wiped by one of them. Living out here is
/// what makes mastery survive by construction rather than by special case.
///
/// Connection details come from ACE's own Config.js at runtime - no credentials in
/// the mod - and the shard db means mastery is covered by the same backups as the
/// characters it belongs to.
/// </summary>
public static class MasteryDb
{
    public const string TableName = "aeshnidae_skill_mastery";

    private static string _connectionString = "";

    public static bool Ready { get; private set; }

    public static string LastError { get; private set; } = "";

    public static void Initialize()
    {
        try
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
                    `character_Id`  INT UNSIGNED NOT NULL,
                    `skill`         INT UNSIGNED NOT NULL,
                    `ranks`         INT          NOT NULL DEFAULT 0,
                    `applied_bonus` INT          NOT NULL DEFAULT 0,
                    `updated`       DATETIME     NOT NULL DEFAULT CURRENT_TIMESTAMP
                                                 ON UPDATE CURRENT_TIMESTAMP,
                    PRIMARY KEY (`character_Id`, `skill`)
                ) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;";
            cmd.ExecuteNonQuery();

            Ready = true;
            LastError = "";
        }
        catch (Exception ex)
        {
            Ready = false;
            LastError = ex.Message;
            ModManager.Log($"[{Mod.Name}] storage unavailable, mastery is disabled: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }

    private static MySqlConnection Open()
    {
        var conn = new MySqlConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // ---------------------------------------------------------------- radiance
    //
    // Mastery is paid for in Radiance, which lives in Aeshnidae.Bank's table on the same
    // shard database. This talks to that table directly rather than to the Bank mod:
    // the mod loader gives each mod its own collectible assembly context, and sharing
    // types across that boundary is exactly the thing Mods\README.md warns will bite.
    // The table's shape is stable and the guarded UPDATE below is the same statement
    // BankDb.TryAdjust uses, so two writers cannot drive a balance negative between them.
    //
    // What this cannot see is Bank's in-memory buffer of earnings not yet flushed (up to
    // its FlushSeconds, ten by default). A player who just killed something may be told
    // they are a few thousand short for a few seconds. The refusal message says so.

    private const string BankTable = "aeshnidae_bank";
    private const string RadianceKey = "Radiance";

    /// <summary>Banked Radiance for an account, as last written by Aeshnidae.Bank.</summary>
    public static long RadianceBalance(uint accountId)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT `amount` FROM `{BankTable}` WHERE `account_Id` = @a AND `currency` = @c;";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@c", RadianceKey);

        var result = cmd.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>
    /// Takes Radiance from an account, refusing rather than going negative. The guard is
    /// in the UPDATE itself, so a purchase racing a transfer cannot overdraw.
    /// </summary>
    public static bool TryDebitRadiance(uint accountId, long amount, out long remaining)
    {
        remaining = 0;

        using var conn = Open();
        using var tx = conn.BeginTransaction();

        int rows;
        using (var update = conn.CreateCommand())
        {
            update.Transaction = tx;
            update.CommandText = $@"
                UPDATE `{BankTable}` SET `amount` = `amount` - @d
                WHERE `account_Id` = @a AND `currency` = @c AND `amount` - @d >= 0;";
            update.Parameters.AddWithValue("@a", accountId);
            update.Parameters.AddWithValue("@c", RadianceKey);
            update.Parameters.AddWithValue("@d", amount);
            rows = update.ExecuteNonQuery();
        }

        if (rows > 0)
        {
            using var select = conn.CreateCommand();
            select.Transaction = tx;
            select.CommandText = $"SELECT `amount` FROM `{BankTable}` WHERE `account_Id` = @a AND `currency` = @c;";
            select.Parameters.AddWithValue("@a", accountId);
            select.Parameters.AddWithValue("@c", RadianceKey);
            remaining = Convert.ToInt64(select.ExecuteScalar() ?? 0L);
        }

        tx.Commit();
        return rows > 0;
    }

    /// <summary>Every mastery row for a character: skill -> ranks.</summary>
    public static Dictionary<Skill, int> Load(uint characterId)
    {
        var result = new Dictionary<Skill, int>();

        if (!Ready)
            return result;

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = $"SELECT `skill`, `ranks` FROM `{TableName}` WHERE `character_Id` = @c;";
            cmd.Parameters.AddWithValue("@c", characterId);

            using var reader = cmd.ExecuteReader();

            while (reader.Read())
                result[(Skill)reader.GetUInt32(0)] = reader.GetInt32(1);
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] failed to read mastery for 0x{characterId:X8}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }

        return result;
    }

    public static void Save(uint characterId, Skill skill, int ranks)
    {
        if (!Ready)
            return;

        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = $@"
                INSERT INTO `{TableName}` (`character_Id`, `skill`, `ranks`)
                VALUES (@c, @s, @r)
                ON DUPLICATE KEY UPDATE `ranks` = @r;";

            cmd.Parameters.AddWithValue("@c", characterId);
            cmd.Parameters.AddWithValue("@s", (uint)skill);
            cmd.Parameters.AddWithValue("@r", ranks);

            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] failed to save mastery for 0x{characterId:X8} {skill}: {ex.Message}",
                           ModManager.LogLevel.Error);
        }
    }
}
