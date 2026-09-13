namespace Aeshnidae.FairPlay;

/// <summary>
/// Local VPN / datacentre address matching.
///
/// Deliberately a downloaded list matched in-process rather than an API lookup. A
/// per-login API call puts a third party on the login path, needs a key, and fails
/// silently when a quota runs out - all to answer a question that a text file of CIDR
/// ranges answers just as well, offline, for nothing.
///
/// The lists come from X4BNet/lists_vpn (VPN and datacentre ranges, rebuilt daily) via
/// the refresh-vpn-lists.sh cron job; any file of one-CIDR-per-line works.
///
/// IPv4 only for now. AC clients connect over IPv4 and every address seen on this
/// server so far is v4; a v6 login simply never matches and is treated as not-VPN,
/// which fails in the permissive direction.
/// </summary>
internal sealed class VpnList
{
    /// <summary>prefix length -> the set of network addresses at that length.</summary>
    private readonly Dictionary<int, HashSet<uint>> _byPrefix = new();

    public int Ranges { get; private set; }
    public DateTime LoadedUtc { get; private set; }
    public string Source { get; private set; } = "";

    public static VpnList Load(string directory)
    {
        var list = new VpnList { Source = directory };

        try
        {
            if (!Directory.Exists(directory))
                return list;

            foreach (var file in Directory.GetFiles(directory, "*.txt"))
            {
                foreach (var raw in File.ReadLines(file))
                {
                    var line = raw.Trim();

                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;

                    list.Add(line);
                }
            }

            list.LoadedUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            ModManager.Log($"[{Mod.Name}] could not load VPN lists from {directory}: {ex.Message}",
                           ModManager.LogLevel.Warn);
        }

        return list;
    }

    private void Add(string cidr)
    {
        // Accept both "1.2.3.0/24" and a bare "1.2.3.4".
        var slash = cidr.IndexOf('/');
        var addressPart = slash < 0 ? cidr : cidr[..slash];
        var prefix = 32;

        if (slash >= 0 && (!int.TryParse(cidr[(slash + 1)..], out prefix) || prefix is < 0 or > 32))
            return;

        if (!TryToUInt(addressPart, out var address))
            return;

        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

        if (!_byPrefix.TryGetValue(prefix, out var set))
            _byPrefix[prefix] = set = new HashSet<uint>();

        if (set.Add(address & mask))
            Ranges++;
    }

    /// <summary>
    /// Is this address inside any listed range?
    ///
    /// At most 33 hash lookups - one per distinct prefix length present - rather than a
    /// scan of every range, which matters because the combined lists run to six figures.
    /// </summary>
    public bool Contains(string? ip)
    {
        if (string.IsNullOrEmpty(ip) || Ranges == 0 || !TryToUInt(ip, out var address))
            return false;

        foreach (var (prefix, set) in _byPrefix)
        {
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

            if (set.Contains(address & mask))
                return true;
        }

        return false;
    }

    private static bool TryToUInt(string ip, out uint value)
    {
        value = 0;

        if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var bytes = parsed.GetAddressBytes();
        value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        return true;
    }
}
