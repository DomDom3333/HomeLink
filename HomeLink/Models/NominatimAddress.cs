using System.Text.Json.Serialization;

namespace HomeLink.Models;

public class NominatimAddress
{
    [JsonPropertyName("suburb")]
    public string? Suburb { get; set; }

    [JsonPropertyName("city_district")]
    public string? CityDistrict { get; set; }

    [JsonPropertyName("district")]
    public string? District { get; set; }

    [JsonPropertyName("city")]
    public string? City { get; set; }

    [JsonPropertyName("town")]
    public string? Town { get; set; }

    [JsonPropertyName("village")]
    public string? Village { get; set; }

    [JsonPropertyName("municipality")]
    public string? Municipality { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }
}