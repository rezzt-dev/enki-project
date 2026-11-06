using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Localization;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Contract tests for <see cref="PolarAuthService"/> against a scripted fake transport
/// (<see cref="FakeHttpMessageHandler"/>) — the link/refresh half of the network layer that used to be
/// untestable because everything short-circuited on the file-based, static
/// <see cref="SupabaseConfig.IsConfigured"/> (10.2 §6/§7).</summary>
public class PolarAuthServiceTests : IDisposable
{
    private const string BaseUrl = "https://fake.supabase.test";
    private const string AnonKey = "anon-key-123";

    private readonly TestPolarCredentialStore _credentialStore = new();
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeHttpMessageHandler _handler = new();
    private readonly SettingsService _settingsService;
    private readonly PolarAuthService _auth;

    public PolarAuthServiceTests()
    {
        _settingsService = new SettingsService(_storageDirectory, _credentialStore);
        _auth = new PolarAuthService(_settingsService, _handler, BaseUrl, AnonKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    // ===================== Configuración =====================

    [Fact]
    public void IsConfigured_TrueWhenBothUrlAndAnonKeyAreProvided() => Assert.True(_auth.IsConfigured);

    [Fact]
    public async Task LinkAsync_NotConfigured_FailsWithoutAnyHttpCall()
    {
        // Empty (not null) so this doesn't fall back to a real, developer-machine local.properties via
        // SupabaseConfig — "??=" only kicks in for a null argument, keeping this test deterministic
        // regardless of the environment it runs in.
        var unconfigured = new PolarAuthService(_settingsService, _handler, baseUrl: "", anonKey: "");

        var (success, error) = await unconfigured.LinkAsync("user@example.com", "hunter2");

        Assert.False(success);
        Assert.Equal(LocalizationService.Instance["Polar_Auth_Error_NoConfig"], error);
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task LinkAsync_MissingCredentials_FailsWithoutAnyHttpCall()
    {
        var (success, error) = await _auth.LinkAsync("", "");

        Assert.False(success);
        Assert.Equal(LocalizationService.Instance["Polar_Auth_Error_MissingCredentials"], error);
        Assert.Empty(_handler.Requests);
    }

    // ===================== LinkAsync =====================

    [Fact]
    public async Task LinkAsync_Success_PersistsSessionAndRaisesLinkStateChanged()
    {
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new
        {
            access_token = "access-1",
            refresh_token = "refresh-1",
            expires_in = 3600,
            user = new { id = "user-1", email = "user@example.com" }
        }));

        var raised = 0;
        _auth.LinkStateChanged += () => raised++;

        var (success, error) = await _auth.LinkAsync(" user@example.com ", "hunter2");

        Assert.True(success);
        Assert.Null(error);
        Assert.True(_auth.IsLinked);
        Assert.Equal("user@example.com", _auth.LinkedEmail);
        Assert.Equal("user-1", _auth.UserId);
        Assert.Equal("access-1", _auth.AccessToken);
        Assert.Equal(1, raised);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal($"{BaseUrl}/auth/v1/token?grant_type=password", request.RequestUri!.ToString());
        Assert.Equal(AnonKey, request.Headers.GetValues("apikey").Single());
    }

    [Fact]
    public async Task LinkAsync_StorageFailure_LeavesAccountUnlinked()
    {
        _credentialStore.FailWrites = true;
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new
        {
            access_token = "access-1", refresh_token = "refresh-1", expires_in = 3600,
            user = new { id = "user-1" }
        }));

        var (success, error) = await _auth.LinkAsync("user@example.com", "hunter2");

        Assert.False(success);
        Assert.Equal(LocalizationService.Instance["Polar_Auth_Error_SecureStorage"], error);
        Assert.False(_auth.IsLinked);
        Assert.Null(_settingsService.Settings.PolarAccessToken);
    }

    [Fact]
    public async Task LinkAsync_InvalidCredentials_ReturnsLocalizedErrorWithoutServerText()
    {
        _handler.Enqueue(JsonResponse(HttpStatusCode.BadRequest, new { error_description = "Invalid login credentials" }));

        var (success, error) = await _auth.LinkAsync("user@example.com", "wrong-password");

        Assert.False(success);
        Assert.Equal(LocalizationService.Instance["Polar_Auth_Error_InvalidCredentials"], error);
        Assert.False(_auth.IsLinked);
    }

    [Fact]
    public async Task LinkAsync_ConnectionFailure_ReturnsLocalizedConnectionError()
    {
        _handler.Enqueue(_ => throw new HttpRequestException("Name or service not known"));

        var (success, error) = await _auth.LinkAsync("user@example.com", "hunter2");

        Assert.False(success);
        Assert.Equal(
            LocalizationService.Instance["Polar_Sync_Error_NoConnection"],
            error);
    }

    [Fact]
    public async Task LinkAsync_Timeout_ReturnsLocalizedTimeoutError()
    {
        _handler.Enqueue(_ => throw new TaskCanceledException());

        var (success, error) = await _auth.LinkAsync("user@example.com", "hunter2");

        Assert.False(success);
        Assert.Equal(LocalizationService.Instance["Polar_Auth_Error_Timeout"], error);
    }

    // ===================== EnsureFreshTokenAsync =====================

    [Fact]
    public async Task EnsureFreshTokenAsync_NotLinked_SkipsWithoutAnyHttpCall()
    {
        await _auth.EnsureFreshTokenAsync();

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_TokenStillFresh_SkipsWithoutAnyHttpCall()
    {
        LinkWithToken(expiresInSeconds: 3600);

        await _auth.EnsureFreshTokenAsync();

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_NearExpiry_RefreshesAndPersistsNewTokens()
    {
        LinkWithToken(expiresInSeconds: 30); // inside the 60s refresh window
        _handler.Enqueue(JsonResponse(HttpStatusCode.OK, new
        {
            access_token = "access-2",
            refresh_token = "refresh-2",
            expires_in = 3600
        }));

        await _auth.EnsureFreshTokenAsync();

        Assert.Equal("access-2", _auth.AccessToken);
        Assert.Equal("refresh-2", _settingsService.Settings.PolarRefreshToken);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal($"{BaseUrl}/auth/v1/token?grant_type=refresh_token", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_RefreshRejected_UnlinksAndRaisesSessionExpired()
    {
        LinkWithToken(expiresInSeconds: 30);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var sessionExpired = 0;
        _auth.SessionExpired += () => sessionExpired++;

        await _auth.EnsureFreshTokenAsync();

        Assert.False(_auth.IsLinked);
        Assert.Null(_auth.AccessToken);
        Assert.Equal(1, sessionExpired);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_RefreshFailsWithServerError_KeepsSessionForTheNextAttempt()
    {
        LinkWithToken(expiresInSeconds: 30);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var sessionExpired = 0;
        _auth.SessionExpired += () => sessionExpired++;

        await _auth.EnsureFreshTokenAsync();

        // A 503 means Supabase itself is having trouble, not that the refresh token was rejected —
        // unlinking here would force a needless re-login every time the server hiccups.
        Assert.True(_auth.IsLinked);
        Assert.Equal("access-stale", _auth.AccessToken);
        Assert.Equal(0, sessionExpired);
    }

    [Fact]
    public async Task EnsureFreshTokenAsync_Offline_KeepsStaleTokenForTheNextAttempt()
    {
        LinkWithToken(expiresInSeconds: 30);
        _handler.Enqueue(_ => throw new HttpRequestException("offline"));

        await _auth.EnsureFreshTokenAsync();

        // No forced unlink on a mere connectivity failure — only an explicit server rejection unlinks.
        Assert.True(_auth.IsLinked);
        Assert.Equal("access-stale", _auth.AccessToken);
    }

    private void LinkWithToken(int expiresInSeconds)
    {
        var settings = _settingsService.Settings;
        settings.PolarLinked = true;
        settings.PolarEmail = "user@example.com";
        settings.PolarUserId = "user-1";
        settings.PolarAccessToken = "access-stale";
        settings.PolarRefreshToken = "refresh-stale";
        settings.PolarAccessTokenExpiresAtUtcMs = DateTimeOffset.UtcNow.AddSeconds(expiresInSeconds).ToUnixTimeMilliseconds();
        _settingsService.Save();
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object payload) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
    };
}
