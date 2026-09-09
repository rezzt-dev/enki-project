using System;
using System.Globalization;
using System.Net.Http.Json;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using enki.Localization;

namespace enki.Services;

/// <summary>Signs in against Supabase Auth (GoTrue) with an email/password that already exists in the
/// shared Polar/Enki Supabase project (docs/supabase-sync/08) — Enki never creates accounts, only links
/// to one, matching "correo y password que ya están configurados en Supabase" from the feature request.
/// Session tokens are persisted via <see cref="SettingsService"/> so the link survives an app restart;
/// <see cref="EnsureFreshTokenAsync"/> silently refreshes the access token before it expires, called by
/// <see cref="SupabaseRestClient"/> before every request.</summary>
// CA1001: _http (HttpClient) is disposable, but this service is an app-lifetime singleton — same rationale
// as PolarSyncService (Fase 10, docs/analisis-proyecto/roadmap.md).
#pragma warning disable CA1001
public sealed class PolarAuthService
#pragma warning restore CA1001
{
    /// <summary>Same reasoning as <see cref="SupabaseRestClient"/>'s request timeout — auth calls must not
    /// hang for the default 100 s <see cref="HttpClient"/> timeout.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly SettingsService _settingsService;
    private readonly HttpClient _http;
    private readonly string? _anonKey;
    private readonly bool _isConfigured;

    /// <summary>Raised after link, unlink, or a forced unlink caused by an expired/revoked refresh
    /// token — lets the Kanban switch and the Settings › Privacidad panel refresh without polling.</summary>
    public event Action? LinkStateChanged;

    /// <summary>Raised specifically when <see cref="EnsureFreshTokenAsync"/> unlinks the account because
    /// the refresh token was rejected by Supabase — a subset of <see cref="LinkStateChanged"/> that the
    /// compositor (<see cref="ViewModels.MainWindowViewModel"/>) uses to push a
    /// <c>NotificationService</c> alert, since this specific case (unlike a user-initiated unlink from
    /// Ajustes) happens silently in the background and the user has no other way to notice.</summary>
    public event Action? SessionExpired;

    /// <summary>True once both a Supabase URL and anon key are known — from <see cref="SupabaseConfig"/>
    /// in production, or from the constructor's test overrides. Exposed so <c>SettingsViewModel</c> reads
    /// this instance's actual configuration instead of the static <see cref="SupabaseConfig"/> directly,
    /// keeping a single source of truth.</summary>
    public bool IsConfigured => _isConfigured;

    public bool IsLinked => _isConfigured && _settingsService.Settings.PolarLinked;
    public string? LinkedEmail => _settingsService.Settings.PolarEmail;
    public string? UserId => _settingsService.Settings.PolarUserId;
    public string? AccessToken => _settingsService.Settings.PolarAccessToken;

    /// <summary>Production callers omit every optional parameter and get the real Supabase project
    /// configured via <see cref="SupabaseConfig"/>. Tests pass <paramref name="httpHandler"/> (a fake
    /// transport) together with a fake <paramref name="baseUrl"/>/<paramref name="anonKey"/> so the full
    /// link/refresh contract can be exercised without a live network dependency or a real
    /// <c>local.properties</c> — see <c>PolarAuthServiceTests</c>.</summary>
    public PolarAuthService(SettingsService settingsService, HttpMessageHandler? httpHandler = null, string? baseUrl = null, string? anonKey = null)
    {
        _settingsService = settingsService;
        baseUrl ??= SupabaseConfig.Url;
        _anonKey = anonKey ?? SupabaseConfig.AnonKey;
        _isConfigured = !string.IsNullOrWhiteSpace(baseUrl) && !string.IsNullOrWhiteSpace(_anonKey);
        _http = new HttpClient(httpHandler ?? new HttpClientHandler())
        {
            BaseAddress = _isConfigured ? new Uri(baseUrl!.TrimEnd('/') + "/") : null,
            Timeout = RequestTimeout
        };
    }

    public async Task<(bool Success, string? Error)> LinkAsync(string email, string password, CancellationToken ct = default)
    {
        if (!_isConfigured)
            return (false, LocalizationService.Instance["Polar_Auth_Error_NoConfig"]);

        email = email.Trim();
        if (email.Length == 0 || password.Length == 0)
            return (false, LocalizationService.Instance["Polar_Auth_Error_MissingCredentials"]);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/v1/token?grant_type=password");
            request.Headers.Add("apikey", _anonKey);
            request.Content = JsonContent.Create(new { email, password });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var key = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.TooManyRequests => "Polar_Sync_Error_TooManyRequests",
                    _ when (int)response.StatusCode >= 500 => "Polar_Sync_Error_ServerError",
                    _ => "Polar_Auth_Error_InvalidCredentials"
                };
                return (false, LocalizationService.Instance[key]);
            }

            var body = await response.Content.ReadFromJsonAsync<AuthTokenDto>(cancellationToken: ct);
            if (body is null || string.IsNullOrEmpty(body.AccessToken))
                return (false, LocalizationService.Instance["Polar_Auth_Error_UnexpectedResponse"]);

            ApplySession(email, body);
            return (true, null);
        }
        catch (HttpRequestException ex)
        {
            LogService.Warning("No se pudo conectar con Polar", ex);
            return (false, LocalizationService.Instance["Polar_Sync_Error_NoConnection"]);
        }
        catch (TaskCanceledException)
        {
            return (false, LocalizationService.Instance["Polar_Auth_Error_Timeout"]);
        }
    }

    public void Unlink()
    {
        var settings = _settingsService.Settings;
        settings.PolarLinked = false;
        settings.PolarUserId = null;
        settings.PolarEmail = null;
        settings.PolarAccessToken = null;
        settings.PolarRefreshToken = null;
        settings.PolarAccessTokenExpiresAtUtcMs = 0;
        settings.PolarLastSyncAtUtcMs = 0;
        _settingsService.Save();
        LinkStateChanged?.Invoke();
    }

    /// <summary>Refreshes the access token when it's within a minute of expiring. Called by
    /// <see cref="SupabaseRestClient"/> before every REST call — no separate background timer needed for
    /// this since it always piggybacks on an actual sync attempt.</summary>
    public async Task EnsureFreshTokenAsync(CancellationToken ct = default)
    {
        var settings = _settingsService.Settings;
        if (!IsLinked || string.IsNullOrEmpty(settings.PolarRefreshToken)) return;

        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(settings.PolarAccessTokenExpiresAtUtcMs);
        if (expiresAt - DateTimeOffset.UtcNow > TimeSpan.FromSeconds(60)) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token");
            request.Headers.Add("apikey", _anonKey);
            request.Content = JsonContent.Create(new { refresh_token = settings.PolarRefreshToken });

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                // GoTrue rejects an invalid/expired/revoked refresh token with 400 (invalid_grant) or
                // 401 — that session can never recover, so stop trying and ask the user to re-link. Any
                // other status (429/5xx/...) is a transient server problem, not a rejected session:
                // unlinking here would force a needless re-login every time Supabase hiccups, so keep the
                // stale token and let the next sync attempt retry, same as the offline branch below.
                if (response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Unauthorized)
                {
                    Unlink();
                    SessionExpired?.Invoke();
                }
                return;
            }

            var body = await response.Content.ReadFromJsonAsync<AuthTokenDto>(cancellationToken: ct);
            if (body is null || string.IsNullOrEmpty(body.AccessToken)) return;

            settings.PolarAccessToken = body.AccessToken;
            settings.PolarRefreshToken = body.RefreshToken ?? settings.PolarRefreshToken;
            settings.PolarAccessTokenExpiresAtUtcMs = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn).ToUnixTimeMilliseconds();
            _settingsService.Save();
        }
        catch (HttpRequestException)
        {
            // Offline — keep the (possibly stale) token; the next sync attempt will retry the refresh.
        }
        catch (TaskCanceledException)
        {
            // Timed out — same as offline, retried on the next sync tick.
        }
    }

    private void ApplySession(string email, AuthTokenDto body)
    {
        var settings = _settingsService.Settings;
        settings.PolarLinked = true;
        settings.PolarEmail = email;
        settings.PolarUserId = body.User?.Id;
        settings.PolarAccessToken = body.AccessToken;
        settings.PolarRefreshToken = body.RefreshToken;
        settings.PolarAccessTokenExpiresAtUtcMs = DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn).ToUnixTimeMilliseconds();
        _settingsService.Save();
        LinkStateChanged?.Invoke();
    }

}
