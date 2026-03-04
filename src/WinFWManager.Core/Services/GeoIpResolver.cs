using System.Collections.Concurrent;
using System.Net;
using MaxMind.GeoIP2;
using WinFWManager.Core.Models;

namespace WinFWManager.Core.Services;

public class GeoIpResolver : IGeoIpResolver
{
    private readonly DatabaseReader? _reader;
    private readonly ConcurrentDictionary<IPAddress, GeoInfo> _geoCache = new();
    private readonly ConcurrentDictionary<IPAddress, (string? Hostname, DateTime CachedAt)> _dnsCache = new();
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);

    public GeoIpResolver(string? mmdbPath)
    {
        if (mmdbPath != null && File.Exists(mmdbPath))
        {
            _reader = new DatabaseReader(mmdbPath);
        }
    }

    public GeoInfo Resolve(IPAddress address)
    {
        if (_geoCache.TryGetValue(address, out var cached))
            return cached;

        var info = ResolveInternal(address);
        _geoCache[address] = info;
        return info;
    }

    public async Task<string?> ReverseDnsAsync(IPAddress address)
    {
        if (_dnsCache.TryGetValue(address, out var cached))
        {
            if (DateTime.UtcNow - cached.CachedAt < DnsCacheTtl)
                return cached.Hostname;
            _dnsCache.TryRemove(address, out _);
        }

        try
        {
            var entry = await Dns.GetHostEntryAsync(address);
            var hostname = entry.HostName;
            _dnsCache[address] = (hostname, DateTime.UtcNow);
            return hostname;
        }
        catch
        {
            _dnsCache[address] = (null, DateTime.UtcNow);
            return null;
        }
    }

    public void Dispose()
    {
        _reader?.Dispose();
        GC.SuppressFinalize(this);
    }

    private GeoInfo ResolveInternal(IPAddress address)
    {
        if (IsPrivateAddress(address))
        {
            return new GeoInfo { IsPrivate = true };
        }

        if (_reader == null)
        {
            return new GeoInfo { IsPrivate = false };
        }

        try
        {
            var response = _reader.City(address);
            return new GeoInfo
            {
                Country = response.Country.Name,
                CountryCode = response.Country.IsoCode,
                City = response.City.Name,
                IsPrivate = false
            };
        }
        catch
        {
            return new GeoInfo { IsPrivate = false };
        }
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return address.Equals(IPAddress.IPv6Loopback);
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || bytes[0] == 127;
    }
}
