using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Comprueba recurrencias, excepciones, caché local y recuperación tras fallos de red.</summary>
public sealed class ExternalCalendarServiceTests
{
    private const string Feed = """
        BEGIN:VCALENDAR
        VERSION:2.0
        PRODID:-//Enki Tests//ES
        BEGIN:VEVENT
        UID:weekly-1
        DTSTAMP:20261001T000000Z
        DTSTART:20261001T100000Z
        DTEND:20261001T110000Z
        RRULE:FREQ=WEEKLY;COUNT=4
        EXDATE:20261008T100000Z
        SUMMARY:Reunión semanal
        DESCRIPTION:Equipo
        END:VEVENT
        BEGIN:VEVENT
        UID:day-1
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261015
        DTEND;VALUE=DATE:20261016
        SUMMARY:Todo el día
        END:VEVENT
        BEGIN:VEVENT
        UID:trip-1
        DTSTAMP:20261001T000000Z
        DTSTART;VALUE=DATE:20261014
        DTEND;VALUE=DATE:20261016
        SUMMARY:Viaje
        END:VEVENT
        END:VCALENDAR
        """;

    private sealed class Handler : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Response { get; set; } = () => new(HttpStatusCode.OK)
        {
            Content = new StringContent(Feed)
        };
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Response());
        }
    }

    [Fact]
    public async Task Refresh_ExpandsRecurrenceAndExceptions_AndRestoresCachedFeed()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-external-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler();
            using var client = new HttpClient(handler);
            using (var service = new ExternalCalendarService(directory, client))
            {
                var subscription = service.Add("Trabajo", "https://calendar.example/private.ics");
                await service.RefreshAsync(subscription.Id);
                var events = service.GetEvents(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                    new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
                Assert.Equal(6, events.Count);
                Assert.DoesNotContain(events, e => e.StartUtc == new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc));
                Assert.Contains(events, e => e.IsAllDay && e.Title == "Todo el día");
                Assert.Equal(2, events.Count(e => e.Title == "Viaje"));
                Assert.All(events, e => Assert.Equal("Trabajo", e.Feed.Name));
            }

            using var reopened = new ExternalCalendarService(directory, client);
            Assert.Equal(6, reopened.GetEvents(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).Count);
            handler.Response = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            await reopened.RefreshAllAsync(force: true);
            Assert.Equal(6, reopened.GetEvents(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("http://calendar.example/a.ics")]
    [InlineData("https://user:secret@calendar.example/a.ics")]
    [InlineData("file:///tmp/events.ics")]
    public void Add_RejectsUnsafeUrl(string url)
    {
        Assert.False(ExternalCalendarService.IsValidUrl(url));
    }

    [Fact]
    public void Add_ConvertsAppleWebcalLinkToHttps()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-external-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new ExternalCalendarService(directory);
            var feed = service.Add("iCloud", "webcal://p01-caldav.icloud.com/published/test");
            Assert.StartsWith("https://", feed.Url, StringComparison.Ordinal);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Refresh_FollowsHttpsRedirect_AndKeepsCachedEventsOnNotModified()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-external-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new Handler();
            using var client = new HttpClient(handler);
            using var service = new ExternalCalendarService(directory, client);
            var feed = service.Add("Trabajo", "https://calendar.example/feed.ics");
            // El segundo intento devuelve contenido desde el destino de la redirección.
            var redirectCalls = 0;
            handler.Response = () => ++redirectCalls == 1
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://cdn.example/feed.ics") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Feed) };
            await service.RefreshAsync(feed.Id);
            Assert.Equal(2, handler.Calls);
            handler.Response = () => new HttpResponseMessage(HttpStatusCode.NotModified);
            await service.RefreshAsync(feed.Id);
            Assert.Equal(6, service.GetEvents(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc)).Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Aggregation_ProjectsExternalEventAsReadOnlyLayer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-external-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient(new Handler());
            using var external = new ExternalCalendarService(directory, client);
            var feed = external.Add("Trabajo", "https://calendar.example/private.ics");
            await external.RefreshAsync(feed.Id);
            var settings = new SettingsService(directory);
            var aggregate = new CalendarAggregationService(new KanbanService(directory),
                new PolarTaskService(directory), new ReminderService(directory), new PolarAuthService(settings),
                externalCalendarService: external);
            var start = new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Local).ToUniversalTime();
            var end = start.AddDays(1);
            var items = aggregate.BuildItems(start, end)
                .Where(i => i.SourceType == CalendarItemSourceType.ExternalCalendar).ToList();
            Assert.NotEmpty(items);
            Assert.All(items, item =>
            {
                Assert.Equal(CalendarAggregationService.ExternalLayerPrefix + feed.Id, item.LayerKey);
                Assert.False(item.CanEdit);
                Assert.False(item.CanMove);
                Assert.False(item.CanDelete);
                Assert.False(item.CanResize);
                Assert.False(item.CanToggleComplete);
            });
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Refresh_InterpretsFloatingTimeInLocalZone()
    {
        var directory = Path.Combine(Path.GetTempPath(), "enki-external-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string floatingFeed = """
                BEGIN:VCALENDAR
                VERSION:2.0
                PRODID:-//Enki Tests//ES
                BEGIN:VEVENT
                UID:floating-1
                DTSTAMP:20261001T000000Z
                DTSTART:20261015T100000
                DTEND:20261015T110000
                SUMMARY:Hora local
                END:VEVENT
                END:VCALENDAR
                """;
            var handler = new Handler { Response = () => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(floatingFeed)
            } };
            using var client = new HttpClient(handler);
            using var service = new ExternalCalendarService(directory, client);
            var feed = service.Add("Local", "https://calendar.example/floating.ics");
            await service.RefreshAsync(feed.Id);
            var item = Assert.Single(service.GetEvents(new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Local).ToUniversalTime(),
                new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Local).ToUniversalTime()));
            Assert.Equal(new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Local).ToUniversalTime(), item.StartUtc);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
