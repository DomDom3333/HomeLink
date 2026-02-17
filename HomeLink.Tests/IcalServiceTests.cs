using System.Net;
using System.Text;
using HomeLink.Services;

namespace HomeLink.Tests;

public class IcalServiceTests
{
    [Fact]
    public void ParseFromContent_GoogleStyleEvent_ParsesTimezoneDateTime()
    {
        const string ical = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:google-1\nDTSTART;TZID=America/New_York:20260218T090000\nDTEND;TZID=America/New_York:20260218T100000\nSUMMARY:Google Meeting\nEND:VEVENT\nEND:VCALENDAR";

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Single(events);
        Assert.Equal("Google Meeting", events[0].Summary);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 9, 0, 0, TimeSpan.FromHours(-5)), events[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 10, 0, 0, TimeSpan.FromHours(-5)), events[0].End);
    }

    [Fact]
    public void ParseFromContent_AppleStyleEvent_WithAlarmAndDuration_ParsesCorrectly()
    {
        const string ical = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:apple-1\nDTSTART:20260218T090000\nDURATION:PT45M\nSUMMARY:Focus Time\nBEGIN:VALARM\nACTION:DISPLAY\nTRIGGER:-PT10M\nEND:VALARM\nEND:VEVENT\nEND:VCALENDAR";

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Single(events);
        Assert.Equal("Focus Time", events[0].Summary);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 9, 0, 0, TimeSpan.Zero), events[0].Start);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 9, 45, 0, TimeSpan.Zero), events[0].End);
    }

    [Theory]
    [InlineData("TRANSPARENT", "Free")]
    [InlineData("OPAQUE", "Busy")]
    public void ParseFromContent_OutlookPrivateEvent_UsesBusyFreeFallback(string transp, string expectedSummary)
    {
        string ical = $"BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:private-event\nDTSTART:20260218T090000Z\nDTEND:20260218T100000Z\nTRANSP:{transp}\nCLASS:PRIVATE\nEND:VEVENT\nEND:VCALENDAR";

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Single(events);
        Assert.Equal(expectedSummary, events[0].Summary);
        Assert.Null(events[0].Description);
        Assert.Null(events[0].Location);
    }

    [Fact]
    public void ParseFromContent_OutlookBusyStatus_UsesFriendlyFallbackSummary()
    {
        const string ical = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:oof-event\nDTSTART:20260218T090000Z\nDTEND:20260218T100000Z\nX-MICROSOFT-CDO-BUSYSTATUS:OOF\nEND:VEVENT\nEND:VCALENDAR";

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Single(events);
        Assert.Equal("Out of office", events[0].Summary);
    }

    [Fact]
    public void ParseFromContent_OutlookFreeBusy_WithMultipleLinesAndDuration_ParsesAllPeriods()
    {
        const string ical = "BEGIN:VCALENDAR\nBEGIN:VFREEBUSY\nUID:fb-1\nFREEBUSY;FBTYPE=BUSY:20260218T090000Z/20260218T100000Z\nFREEBUSY;FBTYPE=FREE:20260218T100000Z/PT30M\nEND:VFREEBUSY\nEND:VCALENDAR";

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Equal(2, events.Count);
        Assert.Equal("Busy", events[0].Summary);
        Assert.Equal("Free", events[1].Summary);
        Assert.Equal(new DateTimeOffset(2026, 2, 18, 10, 30, 0, TimeSpan.Zero), events[1].End);
    }

    [Fact]
    public void ParseFromContent_ParsesEscapedAndFoldedText()
    {
        const string ical = """
                            BEGIN:VCALENDAR
                            BEGIN:VEVENT
                            UID:event-1
                            DTSTART:20260218T090000Z
                            DTEND:20260218T100000Z
                            SUMMARY:Team Sync
                            DESCRIPTION:Line 1\nLine 2
                             continued
                            LOCATION:Office\, Room 12
                            END:VEVENT
                            END:VCALENDAR
                            """;

        IcalService service = new(new HttpClient());
        IReadOnlyList<Models.IcalEvent> events = service.ParseFromContent(ical);

        Assert.Single(events);
        Assert.Equal("Line 1\nLine 2continued", events[0].Description);
        Assert.Equal("Office, Room 12", events[0].Location);
    }

    [Fact]
    public async Task ParseFromFileAsync_ParsesEventsFromDisk()
    {
        string filePath = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(filePath, "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:file-event\nDTSTART:20260218T120000Z\nDTEND:20260218T123000Z\nEND:VEVENT\nEND:VCALENDAR");

            IcalService service = new(new HttpClient());
            IReadOnlyList<Models.IcalEvent> events = await service.ParseFromFileAsync(filePath);

            Assert.Single(events);
            Assert.Equal("file-event", events[0].Uid);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task ParseFromUrlAsync_ParsesEventsFromHttpResponse()
    {
        const string payload = "BEGIN:VCALENDAR\nBEGIN:VEVENT\nUID:http-event\nDTSTART:20260218T120000Z\nDTEND:20260218T130000Z\nSUMMARY:From URL\nEND:VEVENT\nEND:VCALENDAR";
        HttpClient client = new(new StubMessageHandler(payload));

        IcalService service = new(client);
        IReadOnlyList<Models.IcalEvent> events = await service.ParseFromUrlAsync("https://calendar.example/ical");

        Assert.Single(events);
        Assert.Equal("http-event", events[0].Uid);
        Assert.Equal("From URL", events[0].Summary);
    }

    [Fact]
    public void GetUpcomingEvents_ReturnsOrderedAndLimitedSubset()
    {
        IcalService service = new(new HttpClient());
        DateTimeOffset now = new(2026, 2, 18, 0, 0, 0, TimeSpan.Zero);

        Models.IcalEvent[] events =
        [
            new("past", "Past", null, null, now.AddHours(-1), now, false),
            new("next-2", "Next2", null, null, now.AddHours(3), now.AddHours(4), false),
            new("next-1", "Next1", null, null, now.AddHours(1), now.AddHours(2), false)
        ];

        IReadOnlyList<Models.IcalEvent> upcoming = service.GetUpcomingEvents(events, now, 1);

        Assert.Single(upcoming);
        Assert.Equal("next-1", upcoming[0].Uid);
    }

    private sealed class StubMessageHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/calendar")
            };

            return Task.FromResult(response);
        }
    }
}
