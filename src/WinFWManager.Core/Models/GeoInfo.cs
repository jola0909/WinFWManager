namespace WinFWManager.Core.Models;

public class GeoInfo
{
    public string? Country { get; set; }
    public string? CountryCode { get; set; }
    public string? City { get; set; }
    public string? Asn { get; set; }
    public string? Organization { get; set; }
    public bool IsPrivate { get; set; }

    public string DisplayCountry => IsPrivate ? "Private" : Country ?? "Unknown";
}
