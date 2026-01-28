using System.Text.Json.Serialization;

namespace HomeLink.Models;

internal class NominatimResponse
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("address")]
    public NominatimAddress? Address { get; set; }
}