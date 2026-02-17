namespace HomeLink.Models;

/// <summary>
/// Represents a calendar event parsed from an iCalendar source.
/// </summary>
public sealed record IcalEvent(
    string? Uid,
    string? Summary,
    string? Description,
    string? Location,
    DateTimeOffset? Start,
    DateTimeOffset? End,
    bool IsAllDay
);
