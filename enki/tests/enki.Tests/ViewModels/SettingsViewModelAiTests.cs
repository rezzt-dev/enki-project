using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using enki.Models;
using enki.Services;
using enki.Tests.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Ajustes › Inteligencia artificial: la IA nace apagada, no se enciende sin aceptar el aviso (ni
/// desde su propia sección ni desde la fila "Asistente" de la barra lateral) y sus preferencias viven en el
/// archivo de la instalación, no en el del vault.</summary>
public sealed class SettingsViewModelAiTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly List<(AppPage Page, bool Value)> _sidebarChanges = [];

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (SettingsViewModel Vm, SettingsService Settings, AiChatService Chats, string VaultPath) Create(TestPolarCredentialStore? apiKeys = null)
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vaultPath = Path.Combine(_storageDirectory, "vault");
        var vault = new VaultService(settingsService);
        vault.InitializeVault(vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var restClient = new SupabaseRestClient(authService);
        var syncService = new PolarSyncService(authService, restClient, new PolarTaskService(_storageDirectory),
            new ReminderService(_storageDirectory), settingsService);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        settingsService.Settings.Ai.ClaudeCliPath = Path.Combine(_storageDirectory, "no-existe", "claude");
        var cli = new ClaudeCliService(settingsService, _storageDirectory, apiKeys is null ? null : new AiApiKeyStore(apiKeys));
        var chats = new AiChatService(_storageDirectory);

        var vm = new SettingsViewModel(
            settingsService, new CustomThemeService(settingsService), new BrandLogoService(_storageDirectory), vaultSettingsService,
            vault, new VaultBackupService(vault, _storageDirectory), authService, syncService, notificationService,
            switchVault: (_, _) => Task.FromResult(true), onTitleBarModeChanged: _ => { }, onNotificationsPanelChanged: _ => { },
            onRemindersPanelChanged: _ => { }, onCalendarShowSidebarScrollbarChanged: _ => { },
            onSidebarSectionChanged: (page, value) => _sidebarChanges.Add((page, value)), onSidebarSectionOrderChanged: () => { },
            onCalendarSettingsChanged: () => { }, onDashboardShowScrollbarChanged: _ => { }, onKanbanBoardOrientationChanged: _ => { },
            onPolarShowTaskTagsChanged: _ => { }, onUiZoomChanged: _ => { }, onLanguageChanged: () => { },
            aiCliStatus: new ClaudeCliStatusViewModel(cli, action => action(), new AiBackendService(cli, settingsService, _storageDirectory)), aiChatService: chats);
        return (vm, settingsService, chats, vaultPath);
    }

    [Fact]
    public void DefaultReadAccess_PersistsInInstallation_IndependentlyOfChanges()
    {
        var (vm, settings, _, vaultPath) = Create();
        Assert.False(vm.AiAllowReadAccessByDefault);
        vm.AiAllowReadAccessByDefault = true;
        vm.AiAllowVaultChanges = false;
        var saved = new SettingsService(_storageDirectory).Settings.Ai;
        Assert.True(saved.AllowReadAccessByDefault);
        Assert.False(saved.AllowVaultChanges);
        Assert.DoesNotContain("AllowReadAccessByDefault", File.ReadAllText(Path.Combine(vaultPath, ".enki", "settings.json")));
        vm.AiAllowReadAccessByDefault = false;
        Assert.False(new SettingsService(_storageDirectory).Settings.Ai.AllowReadAccessByDefault);
    }

    [Fact]
    public void ProviderSettings_KeepModelsSeparate_WithoutProbingWhenDisabled()
    {
        var (vm, settings, _, _) = Create();
        vm.AiProvider = vm.AiProviderOptions.Single(p => p.Provider == AiProvider.OpenAi);
        vm.AiCustomModel = "my-openai-model";
        Assert.True(vm.IsAiOpenAi);
        Assert.Equal(AiProvider.OpenAi, vm.AiCliStatus!.Backend!.Provider);
        Assert.Equal("my-openai-model", settings.Settings.Ai.OpenAiModel);
        Assert.False(vm.CheckClaudeCliCommand.CanExecute(null));
        Assert.Equal(ClaudeCliState.Unknown, vm.AiCliStatus.Status.State);
        vm.AiProvider = vm.AiProviderOptions.Single(p => p.Provider == AiProvider.Ollama);
        Assert.True(vm.IsAiLocal);
        vm.AiLocalEndpoint = "http://127.0.0.1:11435";
        Assert.Equal("http://127.0.0.1:11435", settings.Settings.Ai.OllamaEndpoint);
        vm.AiProvider = vm.AiProviderOptions.Single(p => p.Provider == AiProvider.Claude);
        Assert.True(vm.IsAiClaude);
        Assert.Equal("claude-sonnet-5-5", settings.Settings.Ai.DefaultModel);
        Assert.Equal("my-openai-model", settings.Settings.Ai.OpenAiModel);
    }

    [Fact]
    public void Ai_IsOffByDefault_AndAssistantRowIsHidden()
    {
        var (vm, settings, _, _) = Create();

        Assert.False(vm.AiEnabled);
        Assert.False(settings.Settings.Ai.Enabled);
        Assert.False(vm.MainSidebarSections.Concat(vm.BottomSidebarSections).Single(s => s.Key == "Assistant").IsVisible);
        Assert.False(vm.AiStatusVisible);
    }

    [Fact]
    public void EnablingWithoutConsent_OpensNotice_AndKeepsAiOff()
    {
        var (vm, settings, _, _) = Create();

        vm.AiEnabled = true;

        Assert.False(vm.AiEnabled);
        Assert.True(vm.IsAiConsentDialogOpen);
        Assert.False(settings.Settings.Ai.Enabled);
        Assert.Empty(_sidebarChanges);
    }

    [Fact]
    public void SidebarRowToggle_GoesThroughTheSameNotice()
    {
        var (vm, settings, _, _) = Create();

        vm.ToggleSidebarSectionVisibilityCommand.Execute("Assistant");

        Assert.True(vm.IsAiConsentDialogOpen);
        Assert.False(settings.Settings.Ai.Enabled);
        Assert.False(vm.MainSidebarSections.Single(s => s.Key == "Assistant").IsVisible);
    }

    [Fact]
    public void DecliningTheNotice_LeavesEverythingOff()
    {
        var (vm, settings, _, _) = Create();
        vm.AiEnabled = true;

        vm.DeclineAiConsentCommand.Execute(null);

        Assert.False(vm.IsAiConsentDialogOpen);
        Assert.False(vm.AiEnabled);
        Assert.Null(settings.Settings.Ai.ConsentAcceptedUtc);
    }

    [Fact]
    public void AcceptingTheNotice_EnablesAssistant_AndPersistsOutsideTheVault()
    {
        var (vm, settings, _, vaultPath) = Create();
        vm.AiEnabled = true;

        vm.AcceptAiConsentCommand.Execute(null);

        Assert.True(vm.AiEnabled);
        Assert.True(vm.AiStatusVisible);
        Assert.True(settings.Settings.Ai.Enabled);
        Assert.NotNull(settings.Settings.Ai.ConsentAcceptedUtc);
        Assert.Contains((AppPage.Assistant, true), _sidebarChanges);
        Assert.True(vm.MainSidebarSections.Single(s => s.Key == "Assistant").IsVisible);

        var global = JsonNode.Parse(File.ReadAllText(settings.SettingsFilePath))!.AsObject();
        Assert.True(global["Ai"]!["Enabled"]!.GetValue<bool>());
        var vaultSettings = JsonNode.Parse(File.ReadAllText(Path.Combine(vaultPath, ".enki", "settings.json")))!.AsObject();
        Assert.False(vaultSettings.ContainsKey("Ai"));
    }

    [Fact]
    public void AfterConsent_TurningOffAndOnAgain_DoesNotAskTwice()
    {
        var (vm, settings, _, _) = Create();
        vm.AiEnabled = true;
        vm.AcceptAiConsentCommand.Execute(null);

        vm.AiEnabled = false;
        vm.AiEnabled = true;

        Assert.False(vm.IsAiConsentDialogOpen);
        Assert.True(settings.Settings.Ai.Enabled);
        Assert.Equal([(AppPage.Assistant, true), (AppPage.Assistant, false), (AppPage.Assistant, true)], _sidebarChanges);
    }

    [Fact]
    public void Preferences_AreSavedAndClamped()
    {
        var (vm, settings, _, _) = Create();

        vm.AiDefaultModel = vm.AiModelOptions.Single(m => m.Alias == "sonnet");
        vm.AiIdleTimeoutSeconds = 5;
        vm.AiMaxContextChars = 50_000;
        vm.AiConfirmBeforeSending = false;
        vm.FlushPendingSave();

        var ai = new SettingsService(_storageDirectory).Settings.Ai;
        Assert.Equal("sonnet", ai.DefaultModel);
        Assert.Equal(AiSettings.MinIdleTimeoutSeconds, ai.IdleTimeoutSeconds);
        Assert.Equal(50_000, ai.MaxContextChars);
        Assert.False(ai.ConfirmBeforeSendingContext);
        Assert.Equal("sonnet", settings.Settings.Ai.DefaultModel);
    }

    [Fact]
    public void ClearHistory_AsksFirst_ThenDeletesEveryChat()
    {
        var (vm, _, chats, _) = Create();
        chats.Save(new AiChatSession { Title = "uno" });
        chats.Save(new AiChatSession { Title = "dos" });
        Assert.True(vm.HasAiHistory);

        vm.RequestClearAiHistoryCommand.Execute(null);
        Assert.True(vm.IsClearAiHistoryConfirmOpen);
        Assert.Equal(2, chats.Sessions.Count);

        vm.ConfirmClearAiHistoryCommand.Execute(null);

        Assert.False(vm.IsClearAiHistoryConfirmOpen);
        Assert.Empty(chats.Sessions);
        Assert.False(vm.HasAiHistory);
    }

    [Fact]
    public void Normalize_DropsEnabledWithoutConsent_AndRepairsOutOfRangeValues()
    {
        var ai = new AiSettings { Enabled = true, IdleTimeoutSeconds = 0, MaxContextChars = 10, ClaudeCliPath = "  /x/claude  " };

        ai.Normalize();

        Assert.False(ai.Enabled);
        Assert.Equal(AiSettings.DefaultIdleTimeoutSeconds, ai.IdleTimeoutSeconds);
        Assert.Equal(AiSettings.DefaultMaxContextChars, ai.MaxContextChars);
        Assert.Equal("/x/claude", ai.ClaudeCliPath);
    }

    [Fact]
    public async Task ClaudeApiKey_IsSavedToTheSecureStore_NeverToSettings_AndCanBeDeleted()
    {
        var store = new TestPolarCredentialStore();
        var (vm, settings, _, _) = Create(store);
        Assert.False(vm.ShowAiApiKey);

        vm.UseClaudeApiKeyCommand.Execute(null);
        Assert.True(settings.Settings.Ai.ClaudeUseApi);
        Assert.True(vm.ShowAiApiKey);
        Assert.False(vm.HasAiApiKey);

        vm.AiApiKeyDraft = "no-es-una-clave";
        await vm.SaveAiApiKeyCommand.ExecuteAsync(null);
        Assert.NotNull(vm.AiApiKeyError);
        Assert.Null(store.Credentials);

        const string key = "sk-ant-api03-0123456789abcdefWXYZ";
        vm.AiApiKeyDraft = key;
        await vm.SaveAiApiKeyCommand.ExecuteAsync(null);
        Assert.Null(vm.AiApiKeyError);
        Assert.Equal(string.Empty, vm.AiApiKeyDraft);
        Assert.Equal(key, store.Credentials!.AccessToken);
        Assert.Equal("sk-ant-…WXYZ", vm.AiApiKeyMasked);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(_storageDirectory, "settings.json")), StringComparison.Ordinal);

        await vm.DeleteAiApiKeyCommand.ExecuteAsync(null);
        Assert.Null(store.Credentials);
        Assert.False(vm.HasAiApiKey);

        vm.UseClaudeSubscriptionCommand.Execute(null);
        Assert.False(settings.Settings.Ai.ClaudeUseApi);
    }

    [Fact]
    public async Task ClaudeApiKey_StoreFailure_IsExplained_AndNothingIsLost()
    {
        var store = new TestPolarCredentialStore { FailWrites = true };
        var (vm, _, _, _) = Create(store);
        vm.UseClaudeApiKeyCommand.Execute(null);
        vm.AiApiKeyDraft = "sk-ant-api03-0123456789abcdefWXYZ";

        await vm.SaveAiApiKeyCommand.ExecuteAsync(null);

        Assert.NotNull(vm.AiApiKeyError);
        Assert.Equal("sk-ant-api03-0123456789abcdefWXYZ", vm.AiApiKeyDraft);
        Assert.False(vm.HasAiApiKey);
    }
}
