using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre <see cref="UpdateCheckService"/> (T9). Dos costuras hacen que no haga falta ni red ni
/// esperar un día: el <see cref="HttpMessageHandler"/> inyectable —mismo patrón que
/// <c>SupabaseRestClient</c>/<c>WebClipService</c>— y el reloj inyectable, como el
/// <c>PopDueReminders(DateTime)</c> de <see cref="ReminderService"/>.</summary>
public class UpdateCheckServiceTests : IDisposable
{
    private const string ApiUrl = "https://api.github.com/repos/rezzt-dev/enki-project/releases/latest";

    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string ReleaseJson(string tag, bool draft = false, bool prerelease = false,
        string url = "https://github.com/rezzt-dev/enki-project/releases/tag/v9.9.9") =>
        JsonSerializer.Serialize(new
        {
            tag_name = tag,
            name = "Enki " + tag,
            html_url = url,
            body = "Notas de la versión.",
            draft,
            prerelease,
            published_at = "2026-09-01T10:00:00Z"
        });

    // ===================== Reglas de decisión (sin red) =====================

    private static UpdateCheckService.GitHubRelease Release(string tag, bool draft = false, bool prerelease = false,
        string url = "https://github.com/rezzt-dev/enki-project/releases/tag/v9.9.9") =>
        new() { TagName = tag, Name = "Enki " + tag, HtmlUrl = url, Body = "Notas.", Draft = draft, PreRelease = prerelease };

    [Fact]
    public void Evaluate_ANewerStableRelease_IsOffered()
    {
        var result = UpdateCheckService.Evaluate(Release("v0.2.0"), "0.1.0", skippedVersion: null);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("0.2.0", result.Update!.Version);
        Assert.Equal("https://github.com/rezzt-dev/enki-project/releases/tag/v9.9.9", result.Update.Url);
    }

    [Theory]
    [InlineData("v0.1.0")]   // la misma
    [InlineData("v0.0.9")]   // anterior
    public void Evaluate_TheSameOrAnOlderRelease_IsNotOffered(string tag) =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateCheckService.Evaluate(Release(tag), "0.1.0", null).Outcome);

    [Fact]
    public void Evaluate_ADraft_IsNotOffered() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateCheckService.Evaluate(Release("v9.9.9", draft: true), "0.1.0", null).Outcome);

    /// <summary>Una prerelease no se ofrece ni marcada como tal en GitHub ni deducida de la etiqueta:
    /// Enki no tiene canal alfa/beta, y quien quiera una la busca a propósito.</summary>
    [Fact]
    public void Evaluate_APreRelease_IsNotOffered()
    {
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateCheckService.Evaluate(Release("v9.9.9", prerelease: true), "0.1.0", null).Outcome);
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateCheckService.Evaluate(Release("v9.9.9-beta.1"), "0.1.0", null).Outcome);
    }

    [Fact]
    public void Evaluate_ASkippedVersion_IsNotOfferedAgain() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, UpdateCheckService.Evaluate(Release("v0.2.0"), "0.1.0", skippedVersion: "0.2.0").Outcome);

    /// <summary>"Omitir esta versión" silencia esa y las anteriores, no las posteriores.</summary>
    [Fact]
    public void Evaluate_AVersionNewerThanTheSkippedOne_IsStillOffered() =>
        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, UpdateCheckService.Evaluate(Release("v0.3.0"), "0.1.0", skippedVersion: "0.2.0").Outcome);

    /// <summary>Criterio de seguridad de T9: "solo abrirá enlaces del repositorio oficial". Un enlace de
    /// otro host descarta el aviso entero en vez de sustituirse en silencio por otro.</summary>
    [Theory]
    [InlineData("https://evil.example.com/releases/tag/v9.9.9")]
    [InlineData("http://github.com/rezzt-dev/enki-project/releases/tag/v9.9.9")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://github.com.evil.example/x")]
    [InlineData(null)]
    public void Evaluate_ARelease_WhoseLinkIsNotTheOfficialRepository_IsDiscarded(string? url) =>
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateCheckService.Evaluate(Release("v9.9.9", url: url!), "0.1.0", null).Outcome);

    [Fact]
    public void Evaluate_AnUnparseableTag_Fails() =>
        Assert.Equal(UpdateCheckOutcome.Failed, UpdateCheckService.Evaluate(Release("latest"), "0.1.0", null).Outcome);

    // ===================== Red simulada, ajuste y límite de frecuencia =====================

    [Fact]
    public async Task CheckAsync_WithANewerRelease_ReturnsIt()
    {
        var settings = new SettingsService(_storageDirectory);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(ReleaseJson("v99.0.0")));
        using var service = new UpdateCheckService(settings, handler, apiUrl: ApiUrl);

        var result = await service.CheckAsync();

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, result.Outcome);
        Assert.Equal("99.0.0", result.Update!.Version);
        Assert.Single(handler.Requests);
    }

    /// <summary>Criterio EARS: "si no hay conexión o el servicio no responde, el sistema no deberá
    /// interrumpir el arranque ni mostrar errores" — aquí, que no escapa ninguna excepción.</summary>
    [Fact]
    public async Task CheckAsync_WhenTheRequestFails_ReportsFailedWithoutThrowing()
    {
        var settings = new SettingsService(_storageDirectory);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(_ => throw new HttpRequestException("sin red"));
        using var service = new UpdateCheckService(settings, handler, apiUrl: ApiUrl);

        Assert.Equal(UpdateCheckOutcome.Failed, (await service.CheckAsync()).Outcome);
    }

    [Fact]
    public async Task CheckAsync_WhenGitHubRateLimits_ReportsFailed()
    {
        var settings = new SettingsService(_storageDirectory);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Forbidden));
        using var service = new UpdateCheckService(settings, handler, apiUrl: ApiUrl);

        Assert.Equal(UpdateCheckOutcome.Failed, (await service.CheckAsync()).Outcome);
    }

    /// <summary>Criterio EARS: "donde «Buscar actualizaciones al iniciar» esté desactivado, el sistema no
    /// deberá realizar ninguna petición de red salvo la comprobación manual".</summary>
    [Fact]
    public async Task CheckOnStartupAsync_WithTheSettingOff_MakesNoRequest()
    {
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.CheckUpdatesOnStartup = false;
        var handler = new FakeHttpMessageHandler();
        using var service = new UpdateCheckService(settings, handler, apiUrl: ApiUrl);

        Assert.Equal(UpdateCheckOutcome.Throttled, (await service.CheckOnStartupAsync()).Outcome);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Criterio EARS: "no deberá comprobar más de una vez cada 24 horas de forma automática".</summary>
    [Fact]
    public async Task CheckOnStartupAsync_WithinTwentyFourHoursOfTheLastCheck_MakesNoRequest()
    {
        var now = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.CheckUpdatesOnStartup = true;
        settings.Settings.LastUpdateCheckUtc = now.AddHours(-3);
        var handler = new FakeHttpMessageHandler();
        using var service = new UpdateCheckService(settings, handler, () => now, ApiUrl);

        Assert.Equal(UpdateCheckOutcome.Throttled, (await service.CheckOnStartupAsync()).Outcome);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckOnStartupAsync_AfterTwentyFourHours_ChecksAgain()
    {
        var now = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.CheckUpdatesOnStartup = true;
        settings.Settings.LastUpdateCheckUtc = now.AddHours(-25);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(ReleaseJson("v99.0.0")));
        using var service = new UpdateCheckService(settings, handler, () => now, ApiUrl);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await service.CheckOnStartupAsync()).Outcome);
        Assert.Single(handler.Requests);
        Assert.Equal(now, settings.Settings.LastUpdateCheckUtc);
    }

    /// <summary>La comprobación manual no mira ni el ajuste ni el límite: si el usuario pulsa el botón, se
    /// comprueba.</summary>
    [Fact]
    public async Task CheckAsync_IgnoresTheSettingAndTheInterval()
    {
        var now = new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.CheckUpdatesOnStartup = false;
        settings.Settings.LastUpdateCheckUtc = now.AddMinutes(-1);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(ReleaseJson("v99.0.0")));
        using var service = new UpdateCheckService(settings, handler, () => now, ApiUrl);

        Assert.Equal(UpdateCheckOutcome.UpdateAvailable, (await service.CheckAsync()).Outcome);
        Assert.Single(handler.Requests);
    }

    /// <summary>GitHub responde 403 a cualquier petición sin <c>User-Agent</c>, así que el encabezado no
    /// es opcional; se comprueba que sale de verdad.</summary>
    [Fact]
    public async Task CheckAsync_SendsAUserAgent()
    {
        var settings = new SettingsService(_storageDirectory);
        var handler = new FakeHttpMessageHandler();
        handler.Enqueue(Json(ReleaseJson("v0.0.1")));
        using var service = new UpdateCheckService(settings, handler, apiUrl: ApiUrl);

        await service.CheckAsync();

        Assert.Contains("Enki", handler.Requests[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }
}
