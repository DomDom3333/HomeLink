using System.Globalization;
using System.Xml;
using HomeLink.Models;

namespace HomeLink.Services;

/// <summary>
/// Parses iCalendar (RFC 5545) content from URLs, files, or raw content.
/// Supports common provider variants (Google, Apple, Outlook), including
/// privacy-restricted and free/busy-only feeds.
/// </summary>
public class IcalService(HttpClient httpClient)
{
    private sealed record IcalProperty(string Name, string Value, Dictionary<string, string> Parameters);

    /// <summary>
    /// Downloads iCal content from a URL and parses events.
    /// </summary>
    public async Task<IReadOnlyList<IcalEvent>> ParseFromUrlAsync(string sourceUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);

        using HttpResponseMessage response = await httpClient.GetAsync(sourceUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseFromContent(content);
    }

    /// <summary>
    /// Loads iCal content from a local file and parses events.
    /// </summary>
    public async Task<IReadOnlyList<IcalEvent>> ParseFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return ParseFromContent(content);
    }

    /// <summary>
    /// Parses iCal content and returns normalized events.
    /// </summary>
    public IReadOnlyList<IcalEvent> ParseFromContent(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        List<string> lines = UnfoldLines(content);
        List<IcalEvent> events = [];

        List<IcalProperty>? currentProperties = null;
        string? currentComponent = null;
        int nestedDepth = 0;

        foreach (string rawLine in lines)
        {
            string line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                string componentName = line[6..];
                if (currentComponent != null)
                {
                    nestedDepth++;
                    continue;
                }

                if (componentName.Equals("VEVENT", StringComparison.OrdinalIgnoreCase)
                    || componentName.Equals("VFREEBUSY", StringComparison.OrdinalIgnoreCase))
                {
                    currentComponent = componentName;
                    currentProperties = [];
                    nestedDepth = 0;
                }

                continue;
            }

            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                string componentName = line[4..];

                if (currentComponent != null && nestedDepth > 0)
                {
                    nestedDepth--;
                    continue;
                }

                if (currentProperties != null
                    && currentComponent != null
                    && componentName.Equals(currentComponent, StringComparison.OrdinalIgnoreCase))
                {
                    if (componentName.Equals("VEVENT", StringComparison.OrdinalIgnoreCase))
                    {
                        events.Add(ToEvent(currentProperties));
                    }
                    else if (componentName.Equals("VFREEBUSY", StringComparison.OrdinalIgnoreCase))
                    {
                        events.AddRange(ToFreeBusyEvents(currentProperties));
                    }

                    currentComponent = null;
                    currentProperties = null;
                }

                continue;
            }

            if (currentProperties == null)
            {
                continue;
            }

            ParsePropertyLine(line, out string name, out string value, out Dictionary<string, string> parameters);
            currentProperties.Add(new IcalProperty(name, value, parameters));
        }

        return events;
    }

    public IReadOnlyList<IcalEvent> GetEventsInRange(IEnumerable<IcalEvent> events, DateTimeOffset rangeStart, DateTimeOffset rangeEnd)
    {
        ArgumentNullException.ThrowIfNull(events);

        return events
            .Where(e => e.Start.HasValue && e.End.HasValue)
            .Where(e => e.Start <= rangeEnd && e.End >= rangeStart)
            .OrderBy(e => e.Start)
            .ToList();
    }

    public IReadOnlyList<IcalEvent> GetUpcomingEvents(IEnumerable<IcalEvent> events, DateTimeOffset fromUtc, int maxCount = 10)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (maxCount <= 0)
        {
            return [];
        }

        return events
            .Where(e => e.Start.HasValue && e.Start.Value >= fromUtc)
            .OrderBy(e => e.Start)
            .Take(maxCount)
            .ToList();
    }

    private static IcalEvent ToEvent(List<IcalProperty> properties)
    {
        string? uid = GetLastValue(properties, "UID");
        string? summary = GetLastValue(properties, "SUMMARY");
        string? description = GetLastValue(properties, "DESCRIPTION");
        string? location = GetLastValue(properties, "LOCATION");

        (DateTimeOffset? Value, bool IsAllDay) start = ParseDateTime(properties, "DTSTART");
        (DateTimeOffset? Value, bool IsAllDay) end = ParseDateTime(properties, "DTEND");

        if (!end.Value.HasValue)
        {
            string? durationRaw = GetLastValue(properties, "DURATION");
            TimeSpan? duration = durationRaw is null ? null : ParseDuration(durationRaw);
            if (start.Value.HasValue && duration.HasValue)
            {
                end = (start.Value.Value.Add(duration.Value), start.IsAllDay);
            }
        }

        return new IcalEvent(
            uid,
            BuildFallbackSummary(properties, summary),
            description,
            location,
            start.Value,
            end.Value,
            start.IsAllDay);
    }

    private static IEnumerable<IcalEvent> ToFreeBusyEvents(List<IcalProperty> properties)
    {
        List<IcalProperty> freeBusyProperties = properties
            .Where(p => p.Name.Equals("FREEBUSY", StringComparison.OrdinalIgnoreCase))
            .ToList();

        int generatedUid = 0;
        foreach (IcalProperty freeBusyProperty in freeBusyProperties)
        {
            string status = BuildBusyStatus(properties, freeBusyProperty.Parameters);
            string summary = status.Equals("FREE", StringComparison.OrdinalIgnoreCase) ? "Free" : "Busy";

            string[] periods = freeBusyProperty.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string period in periods)
            {
                string[] interval = period.Split('/');
                if (interval.Length != 2)
                {
                    continue;
                }

                DateTimeOffset? start = ParseDateTimeValue(interval[0], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
                DateTimeOffset? end = ParseDateTimeValue(interval[1], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

                if (start.HasValue && !end.HasValue)
                {
                    TimeSpan? duration = ParseDuration(interval[1]);
                    if (duration.HasValue)
                    {
                        end = start.Value.Add(duration.Value);
                    }
                }

                if (!start.HasValue || !end.HasValue)
                {
                    continue;
                }

                string uid = GetLastValue(properties, "UID") ?? $"freebusy-{generatedUid++}";
                yield return new IcalEvent(uid, summary, null, null, start, end, false);
            }
        }
    }

    private static string? GetLastValue(List<IcalProperty> properties, string key)
    {
        for (int i = properties.Count - 1; i >= 0; i--)
        {
            IcalProperty property = properties[i];
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return UnescapeText(property.Value);
            }
        }

        return null;
    }

    private static IcalProperty? GetLastProperty(List<IcalProperty> properties, string key)
    {
        for (int i = properties.Count - 1; i >= 0; i--)
        {
            IcalProperty property = properties[i];
            if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return property;
            }
        }

        return null;
    }

    private static (DateTimeOffset? Value, bool IsAllDay) ParseDateTime(List<IcalProperty> properties, string key)
    {
        IcalProperty? property = GetLastProperty(properties, key);
        if (property is null)
        {
            return (null, false);
        }

        bool isAllDay = property.Parameters.TryGetValue("VALUE", out string? type)
            && type.Equals("DATE", StringComparison.OrdinalIgnoreCase);

        DateTimeOffset? parsed = ParseDateTimeValue(property.Value, property.Parameters);
        return (parsed, isAllDay);
    }

    private static DateTimeOffset? ParseDateTimeValue(string value, Dictionary<string, string> parameters)
    {
        if (parameters.TryGetValue("VALUE", out string? valueType)
            && valueType.Equals("DATE", StringComparison.OrdinalIgnoreCase))
        {
            if (DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            {
                return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            }

            return null;
        }

        string[] utcFormats = ["yyyyMMdd'T'HHmmss'Z'", "yyyyMMdd'T'HHmm'Z'"];
        if (DateTimeOffset.TryParseExact(value, utcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset utc))
        {
            return utc.ToUniversalTime();
        }

        if (value.StartsWith("P", StringComparison.OrdinalIgnoreCase) || value.StartsWith("-P", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string[] localFormats = ["yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"];
        if (DateTime.TryParseExact(value, localFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            if (parameters.TryGetValue("TZID", out string? tzid) && !string.IsNullOrWhiteSpace(tzid))
            {
                TimeZoneInfo? timezone = ResolveTimeZone(tzid);
                if (timezone != null)
                {
                    TimeSpan offset = timezone.GetUtcOffset(local);
                    return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
                }
            }

            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.Zero);
        }

        return null;
    }

    private static TimeZoneInfo? ResolveTimeZone(string tzid)
    {
        string normalized = tzid.Trim();

        string mapped = normalized switch
        {
            "W. Europe Standard Time" => "Europe/Vienna",
            "UTC" => "Etc/UTC",
            _ => normalized
        };

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(mapped);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static string BuildFallbackSummary(List<IcalProperty> properties, string? currentSummary)
    {
        if (!string.IsNullOrWhiteSpace(currentSummary))
        {
            return currentSummary;
        }

        string status = BuildBusyStatus(properties, null);
        return status switch
        {
            "FREE" => "Free",
            "OOF" => "Out of office",
            "TENTATIVE" => "Tentative",
            _ => "Busy"
        };
    }

    private static string BuildBusyStatus(List<IcalProperty> properties, Dictionary<string, string>? freeBusyParameters)
    {
        if (freeBusyParameters != null
            && freeBusyParameters.TryGetValue("FBTYPE", out string? fbType)
            && !string.IsNullOrWhiteSpace(fbType))
        {
            return fbType.Trim().ToUpperInvariant();
        }

        string? outlookStatus = GetLastValue(properties, "X-MICROSOFT-CDO-BUSYSTATUS");
        if (!string.IsNullOrWhiteSpace(outlookStatus))
        {
            return outlookStatus.Trim().ToUpperInvariant();
        }

        string? transparency = GetLastValue(properties, "TRANSP");
        if (transparency != null && transparency.Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase))
        {
            return "FREE";
        }

        return "BUSY";
    }

    private static TimeSpan? ParseDuration(string value)
    {
        try
        {
            return XmlConvert.ToTimeSpan(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static List<string> UnfoldLines(string content)
    {
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        string[] rawLines = normalized.Split('\n');

        List<string> unfolded = [];
        foreach (string line in rawLines)
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && unfolded.Count > 0)
            {
                unfolded[^1] += line[1..];
            }
            else
            {
                unfolded.Add(line);
            }
        }

        return unfolded;
    }

    private static void ParsePropertyLine(string line, out string name, out string value, out Dictionary<string, string> parameters)
    {
        int separatorIndex = line.IndexOf(':');
        if (separatorIndex < 0)
        {
            name = line;
            value = string.Empty;
            parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        string lhs = line[..separatorIndex];
        value = line[(separatorIndex + 1)..];

        string[] nameAndParameters = lhs.Split(';');
        name = nameAndParameters[0];

        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < nameAndParameters.Length; i++)
        {
            string part = nameAndParameters[i];
            int equalIndex = part.IndexOf('=');
            if (equalIndex <= 0 || equalIndex >= part.Length - 1)
            {
                continue;
            }

            string paramName = part[..equalIndex];
            string paramValue = part[(equalIndex + 1)..];
            parameters[paramName] = paramValue;
        }
    }

    private static string UnescapeText(string value)
    {
        return value
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\,", ",", StringComparison.Ordinal)
            .Replace("\\;", ";", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
