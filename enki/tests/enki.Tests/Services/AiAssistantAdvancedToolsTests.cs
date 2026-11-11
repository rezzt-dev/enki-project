using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public sealed class AiAssistantAdvancedToolsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SettingsService _settings;
    private readonly VaultService _vault;
    private readonly VaultSettingsService _vaultSettings;
    private readonly AiVaultToolbox _tools;
    private readonly ExternalCalendarService _external;
    private readonly ReminderService _reminders;
    private int _refreshes;

    public AiAssistantAdvancedToolsTests()
    {
        _settings = new SettingsService(_directory);
        _vault = new VaultService(_settings);
        _vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        _vaultSettings = new VaultSettingsService(_vault);
        var kanban = new KanbanService(_directory);
        var polar = new PolarTaskService(_directory);
        var auth = new PolarAuthService(_settings);
        _reminders = new ReminderService(_directory);
        _external = new ExternalCalendarService(_directory);
        _tools = new AiVaultToolbox(_vault, new SessionService(_vault), kanban, polar, auth, _reminders, _settings,
            new CalendarAggregationService(kanban, polar, _reminders, auth, externalCalendarService: _external),
            new AiVaultHost { SettingsChanged = () => _refreshes++, ExportCalendar = _ => Task.FromResult(false) },
            vaultSettingsService: _vaultSettings, externalCalendars: _external);
    }

    private Task<AiToolOutcome> Call(string tool, object args, AiToolContext? context = null) =>
        _tools.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), context ?? new AiToolContext { Scope = AiAccessScope.Vault });

    [Fact]
    public async Task SafeSettings_ConfirmRejectUnknownAndUndoWithoutOverwritingLaterChanges()
    {
        var actions = new HashSet<AiToolActivity>();
        var confirmations = 0;
        var approved = new AiToolContext { Scope = AiAccessScope.Vault, ActivityReported = a => actions.Add(a),
            ApprovalRequested = (_, _) => { confirmations++; return Task.FromResult(true); } };
        Assert.True((await Call("change_safe_setting", new { key = "theme", value = "Light" })).IsError);
        Assert.Equal("Dark", _settings.Settings.Theme);
        Assert.True((await Call("change_safe_setting", new { key = "Provider", value = "Ollama" }, approved)).IsError);
        Assert.True((await Call("change_safe_setting", new { key = "zoom_percent", value = 0 }, approved)).IsError);
        Assert.Equal(0, confirmations);
        Assert.False((await Call("change_safe_setting", new { key = "theme", value = "Light" }, approved)).IsError);
        Assert.Equal(1, confirmations);
        Assert.Equal("Light", _settings.Settings.Theme);
        Assert.Equal(1, _refreshes);
        Assert.Equal(1, _tools.RevertActivities(actions.ToList()).Reverted);
        Assert.Equal("Dark", _settings.Settings.Theme);
        actions.Clear();
        Assert.False((await Call("change_safe_setting", new { key = "zoom_percent", value = 120 }, approved)).IsError);
        _vaultSettings.Settings.UiZoomPercent = 130;
        Assert.Equal(1, _tools.RevertActivities(actions.ToList()).Skipped);
        Assert.Equal(130, _vaultSettings.Settings.UiZoomPercent);
        var safe = await Call("list_safe_settings", new { });
        Assert.DoesNotContain("api", safe.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privacy", safe.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeSettings_DetectChangesDuringApprovalAndRespectReadOnlyMode()
    {
        var context = new AiToolContext { Scope = AiAccessScope.Vault,
            ApprovalRequested = (_, _) => { _vaultSettings.Settings.UiZoomPercent = 140; return Task.FromResult(true); } };
        Assert.True((await Call("change_safe_setting", new { key = "zoom_percent", value = 120 }, context)).IsError);
        Assert.Equal(140, _vaultSettings.Settings.UiZoomPercent);
        _settings.Settings.Ai.AllowVaultChanges = false;
        Assert.True((await Call("change_safe_setting", new { key = "theme", value = "Light" }, context)).IsError);
        Assert.Equal("Dark", _settings.Settings.Theme);
    }

    [Fact]
    public async Task Agenda_FiltersLayersAndCompletedItems_ExternalSubscriptionsHideSecrets()
    {
        var from = new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);
        var reminder = _reminders.Create("Agenda", "", from, "BellOutline");
        _external.Add("Private feed", "https://calendar.example/feed?token=secret-token");
        var layers = await Call("list_calendar_layers", new { });
        Assert.Contains("Private feed", layers.Text);
        Assert.DoesNotContain("secret-token", layers.Text);
        Assert.DoesNotContain("calendar.example", layers.Text);
        var all = await Call("get_agenda", new { from = "2026-11-10", to = "2026-11-11", layer_keys = new[] { "reminder" } });
        Assert.Contains("Agenda", all.Text);
        var actions = new HashSet<AiToolActivity>();
        var approved = new AiToolContext { Scope = AiAccessScope.Vault, ActivityReported = a => actions.Add(a),
            ApprovalRequested = (_, _) => Task.FromResult(true) };
        Assert.False((await Call("set_calendar_layer", new { key = "reminder", visible = false }, approved)).IsError);
        var hidden = await Call("get_agenda", new { from = "2026-11-10", to = "2026-11-11", visible_only = true });
        Assert.DoesNotContain("Agenda", hidden.Text);
        Assert.Equal(1, _tools.RevertActivities(actions.ToList()).Reverted);
        _reminders.ToggleCompleted(reminder.Id);
        var pending = await Call("get_agenda", new { from = "2026-11-10", to = "2026-11-11", include_completed = false });
        Assert.DoesNotContain("Agenda", pending.Text);
        Assert.True((await Call("get_agenda", new { from = "2026-11-10", layer_keys = new[] { "missing" } })).IsError);
        Assert.Contains("false", (await Call("export_calendar_ics", new { })).Text);
    }

    [Fact]
    public async Task ToolAllowlist_IsEnforcedWhenCallingUnadvertisedTools()
    {
        var context = new AiToolContext { Scope = AiAccessScope.Vault, AllowedTools = new HashSet<string> { "read_note" } };
        Assert.Single(_tools.AvailableTools(context));
        Assert.True((await Call("create_note", new { title = "Forbidden" }, context)).IsError);
        Assert.Empty(_vault.GetAllNotesRecursive(_vault.VaultPath));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call("list_safe_settings", new { },
            new AiToolContext { Scope = AiAccessScope.Vault, Cancellation = cancelled.Token }));
    }

    [Fact]
    public async Task ParallelMcpContexts_HaveIndependentTokensScopesAndRevocation()
    {
        using var server = new EnkiMcpServer(_tools);
        var first = server.Register(new AiToolContext { Scope = AiAccessScope.Vault,
            AllowedTools = new HashSet<string> { "list_safe_settings" } });
        var second = server.Register(new AiToolContext { Scope = AiAccessScope.Vault,
            AllowedTools = new HashSet<string> { "list_calendar_layers" } });
        Assert.NotEqual(first.BearerToken, second.BearerToken);
        Assert.Single(await EnkiMcpClient.ListToolsAsync(EnkiMcpClient.LoopbackHttp, first, default));
        Assert.Single(await EnkiMcpClient.ListToolsAsync(EnkiMcpClient.LoopbackHttp, second, default));
        Assert.True((await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, first,
            "list_calendar_layers", null, default)).IsError);
        server.Unregister(first.BearerToken);
        await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() =>
            EnkiMcpClient.ListToolsAsync(EnkiMcpClient.LoopbackHttp, first, default));
        Assert.False((await EnkiMcpClient.CallToolAsync(EnkiMcpClient.LoopbackHttp, second,
            "list_calendar_layers", null, default)).IsError);
    }

    public void Dispose()
    {
        _external.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
