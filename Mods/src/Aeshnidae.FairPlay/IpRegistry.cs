namespace Aeshnidae.FairPlay;

/// <summary>One login: which account, from which address, when.</summary>
public sealed class LoginRecord
{
    [JsonPropertyName("ts")]      public string Timestamp { get; set; } = DateTime.UtcNow.ToString("O");
    [JsonPropertyName("account")] public string Account { get; set; } = "";
    [JsonPropertyName("char")]    public string? Character { get; set; }
    [JsonPropertyName("ip")]      public string Address { get; set; } = "";
}

/// <summary>
/// Login address history, and the correlations worth knowing about.
///
/// Deliberately no third-party IP-reputation service: those cost money, need an
/// outbound call on the login path, and answer a question ("is this a VPN?") that is
/// less useful here than the one the data answers by itself - which accounts share an
/// address, and which account is arriving from unusually many addresses. Someone
/// running six clients through a VPN still shows up as six accounts on one address.
///
/// Stored as JSONL next to the mod, same shape as the audit trail, so it greps the
/// same way.
/// </summary>
internal sealed class IpRegistry : IDisposable
{
    private readonly string _path;
    private readonly Settings _settings;
    private readonly ConcurrentQueue<LoginRecord> _pending = new();

    /// <summary>account -> addresses seen, and address -> accounts seen.</summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _byAccount = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _byAddress = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _writeLock = new();

    private static readonly JsonSerializerOptions LineOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>UTF-8 with no BOM - a BOM in front of the first record breaks strict JSON parsers.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public int KnownAccounts => _byAccount.Count;
    public int KnownAddresses => _byAddress.Count;

    public IpRegistry(string modPath, Settings settings)
    {
        _settings = settings;
        _path = Path.Combine(modPath, "logins.jsonl");
        Load();
    }

    /// <summary>Replays the history so correlations survive a restart.</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var cutoff = _settings.HistoryDays > 0
                ? DateTime.UtcNow.AddDays(-_settings.HistoryDays)
                : DateTime.MinValue;

            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                LoginRecord? rec;
                try { rec = JsonSerializer.Deserialize<LoginRecord>(line); }
                catch { continue; }

                if (rec is null || string.IsNullOrEmpty(rec.Account) || string.IsNullOrEmpty(rec.Address))
                    continue;

                if (DateTime.TryParse(rec.Timestamp, null, DateTimeStyles.RoundtripKind, out var when) && when < cutoff)
                    continue;

                Index(rec.Account, rec.Address);
            }

            RebuildLinks();
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not read login history: {ex.Message}", ModManager.LogLevel.Warn);
        }
    }

    private void Index(string account, string address)
    {
        _byAccount.GetOrAdd(account, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _byAddress.GetOrAdd(address, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        lock (_byAccount[account]) _byAccount[account].Add(address);
        lock (_byAddress[address]) _byAddress[address].Add(account);
    }

    /// <summary>
    /// Records a login and returns anything worth announcing. Called on the login
    /// path, so it must be cheap and must not throw.
    /// </summary>
    public List<string> Record(string account, string? character, string address)
    {
        var notes = new List<string>();

        try
        {
            if (!_settings.RecordLoginAddresses || string.IsNullOrEmpty(account) || string.IsNullOrEmpty(address))
                return notes;

            Index(account, address);
            RebuildLinks();

            var rec = new LoginRecord { Account = account, Character = character, Address = address };
            _pending.Enqueue(rec);
            Flush();

            var accountsHere = Accounts(address);
            if (accountsHere.Count >= _settings.FlagAccountsPerAddress)
                notes.Add($"{address} has now been used by {accountsHere.Count} accounts: {string.Join(", ", accountsHere.Take(8))}");

            var addressesUsed = Addresses(account);
            if (addressesUsed.Count >= _settings.FlagAddressesPerAccount)
                notes.Add($"account {account} has now logged in from {addressesUsed.Count} addresses");
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] login record failed: {ex.Message}", ModManager.LogLevel.Error);
        }

        return notes;
    }

    private void Flush()
    {
        lock (_writeLock)
        {
            var sb = new StringBuilder();

            while (_pending.TryDequeue(out var rec))
                sb.AppendLine(JsonSerializer.Serialize(rec, LineOptions));

            if (sb.Length == 0)
                return;

            try { File.AppendAllText(_path, sb.ToString(), Utf8NoBom); }
            catch (Exception ex)
            {
                ModManager.Log($"[{Mod.Name}] could not append login history: {ex.Message}", ModManager.LogLevel.Error);
            }
        }
    }

    // ---------------------------------------------------------------- linking
    //
    // Live IP is a weak key: a second client behind a VPN gets a different address and
    // every rule stops seeing it as the same person. History does not evaporate, so
    // accounts that have EVER shared an address are treated as one household from then
    // on. Union-find over accounts, with addresses as the edges.

    private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _linkLock = new();

    private string Find(string account)
    {
        if (!_parent.TryGetValue(account, out var p))
        {
            _parent[account] = account;
            return account;
        }

        if (!string.Equals(p, account, StringComparison.OrdinalIgnoreCase))
        {
            var root = Find(p);
            _parent[account] = root;   // path compression
            return root;
        }

        return account;
    }

    private void Union(string a, string b)
    {
        var ra = Find(a);
        var rb = Find(b);

        if (string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase))
            return;

        // Deterministic representative so the household key is stable across restarts.
        if (string.Compare(ra, rb, StringComparison.OrdinalIgnoreCase) <= 0)
            _parent[rb] = ra;
        else
            _parent[ra] = rb;
    }

    /// <summary>
    /// Rebuilds the account clusters from scratch. Addresses in ExemptAddresses do not
    /// create links - that is the whole point of the exemption, since a genuinely
    /// shared connection would otherwise weld strangers together permanently. Accounts
    /// in NeverLink stay in a household of one.
    /// </summary>
    public void RebuildLinks()
    {
        lock (_linkLock)
        {
            _parent.Clear();

            if (!_settings.LinkAccountsBySharedAddress)
                return;

            foreach (var kv in _byAddress)
            {
                // Exempt addresses are declared shared; VPN exit nodes are shared by
                // construction. Neither is evidence that two accounts are one person.
                if (_settings.IsAddressExempt(kv.Key))
                    continue;

                if (_settings.ExcludeVpnFromLinking && Mod.IsVpnAddress(kv.Key))
                    continue;

                var accounts = Snapshot(kv.Value)
                    .Where(a => !_settings.IsNeverLinked(a))
                    .ToList();

                for (var i = 1; i < accounts.Count; i++)
                    Union(accounts[0], accounts[i]);
            }
        }
    }

    /// <summary>Stable key for the household an account belongs to; the account itself if unlinked.</summary>
    public string HouseholdOf(string account)
    {
        if (string.IsNullOrEmpty(account) || !_settings.LinkAccountsBySharedAddress || _settings.IsNeverLinked(account))
            return account ?? "";

        lock (_linkLock) return Find(account);
    }

    /// <summary>Every account in the same household, including this one.</summary>
    public List<string> Household(string account)
    {
        var key = HouseholdOf(account);

        lock (_linkLock)
            return _byAccount.Keys
                .Where(a => string.Equals(Find(a), key, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>All households with more than one account, largest first.</summary>
    public List<List<string>> Households(int minSize = 2)
    {
        lock (_linkLock)
            return _byAccount.Keys
                .GroupBy(a => Find(a), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= minSize)
                .Select(g => g.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
                .OrderByDescending(g => g.Count)
                .ToList();
    }

    public List<string> Accounts(string address) =>
        _byAddress.TryGetValue(address, out var set) ? Snapshot(set) : new List<string>();

    public List<string> Addresses(string account) =>
        _byAccount.TryGetValue(account, out var set) ? Snapshot(set) : new List<string>();

    private static List<string> Snapshot(HashSet<string> set)
    {
        lock (set) return set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Addresses used by more than one account, worst first. Powers /fairplay shared.</summary>
    public List<(string Address, List<string> Accounts)> Shared(int min = 2) =>
        _byAddress
            .Select(kv => (Address: kv.Key, Accounts: Snapshot(kv.Value)))
            .Where(x => x.Accounts.Count >= min)
            .OrderByDescending(x => x.Accounts.Count)
            .ToList();

    public void Dispose() => Flush();
}
