using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Contract tests for <see cref="SupabaseRestClient"/> against a scripted fake transport
/// (<see cref="FakeHttpMessageHandler"/>) — exactly the coverage <c>PolarSyncServiceTests</c> used to call
/// out of scope because the client "always opens a real HttpClient with no injectable transport" (10.2
/// §6/§7). Verifies the request shape (method, path, headers, body) and the pagination contract that
/// <see cref="PolarSyncService"/>'s pull half depends on.</summary>
public class SupabaseRestClientTests : IDisposable
{
    private const string BaseUrl = "https://fake.supabase.test";
    private const string AnonKey = "anon-key-123";

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SupabaseRestClient _rest;

    public SupabaseRestClientTests()
    {
        var settingsService = new SettingsService(_storageDirectory);
        settingsService.Settings.PolarAccessToken = "test-access-token"; // PolarLinked stays false, so
        // EnsureFreshTokenAsync's IsLinked guard short-circuits before it ever needs a network call.
        var authService = new PolarAuthService(settingsService, _handler, BaseUrl, AnonKey);
        _rest = new SupabaseRestClient(authService, _handler, BaseUrl, AnonKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public async Task UpsertAsync_EmptyList_ReturnsEmptyWithoutHttpCall()
    {
        var result = await _rest.UpsertAsync("task_lists", new List<TaskListDto>(), default);

        Assert.Empty(result);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task UpsertAsync_SendsCorrectRequestAndParsesResponse()
    {
        var dto = new TaskListDto { Id = "l1", Title = "Trabajo", UpdatedAt = 1000, CreatedAt = 900 };
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] { dto }));

        var result = await _rest.UpsertAsync("task_lists", [dto], default);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"{BaseUrl}/rest/v1/task_lists?on_conflict=id", request.RequestUri!.ToString());
        Assert.Equal(AnonKey, request.Headers.GetValues("apikey").Single());
        Assert.Equal("Bearer test-access-token", request.Headers.GetValues("Authorization").Single());
        Assert.Equal("resolution=merge-duplicates,return=representation", request.Headers.GetValues("Prefer").Single());

        var body = JsonSerializer.Deserialize<List<TaskListDto>>(_handler.RequestBodies.Single()!);
        Assert.Equal("l1", body!.Single().Id);

        Assert.Equal("l1", Assert.Single(result).Id);
    }

    [Fact]
    public async Task SelectUpdatedSinceAsync_SendsCorrectQueryAndParsesResponse()
    {
        var dto = new TaskDto { Id = "t1", ListId = "l1", Title = "Comprar leche", UpdatedAt = 5000 };
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new[] { dto }));

        var result = await _rest.SelectUpdatedSinceAsync<TaskDto>("tasks", 4000, default);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{BaseUrl}/rest/v1/tasks?updated_at=gt.4000&order=updated_at.asc,id.asc&limit=500", request.RequestUri!.ToString());
        Assert.Equal("t1", Assert.Single(result).Id);
    }

    [Fact]
    public async Task SelectUpdatedSinceAsync_PaginatesUntilAShortPageEndsIt()
    {
        var fullPage = Enumerable.Range(0, 500)
            .Select(i => new ReminderDto { Id = $"r{i}", Title = $"Reminder {i}", UpdatedAt = 1000 + i })
            .ToList();
        var secondPage = new List<ReminderDto> { new() { Id = "r-last", Title = "Última", UpdatedAt = 2000 } };

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, fullPage));
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, secondPage));

        var result = await _rest.SelectUpdatedSinceAsync<ReminderDto>("reminders", 0, default);

        Assert.Equal(501, result.Count);
        Assert.Equal(2, _handler.Requests.Count);
        // The second page's request cursor must advance past the last row of the first (full) page,
        // tie-broken by id so rows sharing that exact millisecond aren't dropped (see next test).
        var secondRequestUri = _handler.Requests[1].RequestUri!.ToString();
        Assert.Contains("updated_at.gt.1499", secondRequestUri);
        Assert.Contains("updated_at.eq.1499", secondRequestUri);
        Assert.Contains("id.gt.r499", secondRequestUri);
    }

    [Fact]
    public async Task SelectUpdatedSinceAsync_TiedTimestampsAtAPageBoundary_DoesNotDropTheRemainingTiedRows()
    {
        // 501 rows all sharing the exact same updated_at (e.g. a bulk push trigger-stamped them in the
        // same transaction) — a bare `updated_at.gt` cursor on the second page would exclude every row
        // tied with the boundary value, silently losing whichever ones didn't fit on the first page.
        var tiedRows = Enumerable.Range(0, 501)
            .Select(i => new ReminderDto { Id = $"r{i:000}", Title = $"Reminder {i}", UpdatedAt = 1000 })
            .ToList();
        var firstPage = tiedRows.Take(500).ToList();
        var secondPage = tiedRows.Skip(500).ToList();

        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, firstPage));
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, secondPage));

        var result = await _rest.SelectUpdatedSinceAsync<ReminderDto>("reminders", 0, default);

        Assert.Equal(501, result.Count);
        Assert.Contains(result, r => r.Id == "r500");
    }

    [Fact]
    public async Task UpsertAsync_ServerRejection_ThrowsHttpRequestExceptionWithStatusCode()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => _rest.UpsertAsync("task_lists", [new TaskListDto { Id = "l1" }], default));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    private static HttpResponseMessage JsonResponse<T>(HttpStatusCode status, T payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };
}
