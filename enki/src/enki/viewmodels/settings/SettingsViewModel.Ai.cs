using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Inteligencia artificial: activar el asistente (con el aviso de privacidad y términos la
/// primera vez), dónde está el CLI de Claude, el modelo y los límites por defecto, y borrar el historial.
/// El interruptor es el mismo que el de la fila "Asistente" de Ajustes › Navegación — <see cref="AiEnabled"/>
/// es el flag de esa sección de la barra lateral —, así que encenderlo desde cualquiera de los dos sitios
/// pasa por el mismo aviso. Ver <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed partial class SettingsViewModel
{
    private AiChatService? _aiChatService;

    /// <summary>Estado del CLI compartido con la sección Asistente. <see langword="null"/> en los tests que
    /// no componen el asistente.</summary>
    public ClaudeCliStatusViewModel? AiCliStatus { get; private set; }

    public ObservableCollection<AiModelOption> AiModelOptions { get; } = [];
    public System.Collections.Generic.IReadOnlyList<AiProviderOption> AiProviderOptions { get; } = AiProviderOption.Build();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiClaude), nameof(IsAiOpenAi), nameof(IsAiLocal))]
    private AiProviderOption? _aiProvider;

    public bool IsAiClaude => AiProvider?.Provider == Models.AiProvider.Claude;
    public bool IsAiOpenAi => AiProvider?.Provider == Models.AiProvider.OpenAi;
    public bool IsAiLocal => AiProvider?.Provider == Models.AiProvider.Ollama;

    // ----- Claude: suscripción o clave de API propia -----

    /// <summary>Claude se usa con la clave de API del usuario (facturación por uso) en vez de con su
    /// suscripción. El CLI es el mismo; solo cambia la credencial con la que Enki lo lanza.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAiApiKey))]
    private bool _aiClaudeUseApi;

    /// <summary>Lo que el usuario pega en el campo de la clave. Se vacía al guardarla: la clave guardada no se
    /// vuelve a enseñar, solo sus últimos caracteres.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAiApiKeyCommand))]
    private string _aiApiKeyDraft = string.Empty;

    [ObservableProperty]
    private string? _aiApiKeyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiApiKeyStatus), nameof(HasAiApiKey))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAiApiKeyCommand))]
    private string? _aiApiKeyMasked;

    public bool HasAiApiKey => AiApiKeyMasked is not null;

    public bool ShowAiApiKey => AiClaudeUseApi && AiCliStatus?.ApiKeys is not null;

    public string AiApiKeyStatus => AiApiKeyMasked is { } masked
        ? string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance["Settings_Ai_ApiKey_Saved"], masked)
        : LocalizationService.Instance["Settings_Ai_ApiKey_None"];

    partial void OnAiClaudeUseApiChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.ClaudeUseApi = value;
        _settingsService.Save();
        if (value) LoadAiApiKeyStatus();
        if (AiEnabled && AiCliStatus is { IsChecking: false }) _ = AiCliStatus.RefreshAsync();
    }

    /// <summary>Solo se pregunta al almacén seguro cuando el modo de clave está activo: en macOS leer el
    /// Keychain puede pedir permiso, y nadie que use su suscripción debería ver ese aviso.</summary>
    private void LoadAiApiKeyStatus()
    {
        var key = AiClaudeUseApi ? AiCliStatus?.ApiKeys?.TryRead() : null;
        AiApiKeyMasked = key is null ? null : AiApiKeyStore.Mask(key);
    }

    private bool CanSaveAiApiKey => AiApiKeyDraft.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveAiApiKey))]
    private async Task SaveAiApiKey()
    {
        if (AiCliStatus?.ApiKeys is not { } store) return;
        var key = AiApiKeyDraft.Trim();
        if (!AiApiKeyStore.LooksLikeApiKey(key))
        {
            AiApiKeyError = LocalizationService.Instance["Ai_ApiKey_Invalid"];
            return;
        }
        try
        {
            store.Write(key);
        }
        catch (Exception ex) when (AiApiKeyStore.IsStoreFailure(ex))
        {
            LogService.Warning("No se pudo guardar la clave de API del asistente en el almacén seguro", ex);
            AiApiKeyError = LocalizationService.Instance["Settings_Ai_ApiKey_StoreFailed"];
            return;
        }
        AiApiKeyError = null;
        AiApiKeyDraft = string.Empty;
        AiApiKeyMasked = AiApiKeyStore.Mask(key);
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Ai_ApiKey_SavedTitle"),
            LocalizedTextFormatter.Format("Settings_Ai_ApiKey_SavedMessage"), NotificationSeverity.Success);
        if (AiEnabled && AiCliStatus is { IsChecking: false }) await AiCliStatus.RefreshAsync();
    }

    [RelayCommand(CanExecute = nameof(HasAiApiKey))]
    private async Task DeleteAiApiKey()
    {
        if (AiCliStatus?.ApiKeys is not { } store) return;
        try
        {
            store.Delete();
        }
        catch (Exception ex) when (AiApiKeyStore.IsStoreFailure(ex))
        {
            LogService.Warning("No se pudo borrar la clave de API del asistente del almacén seguro", ex);
            AiApiKeyError = LocalizationService.Instance["Settings_Ai_ApiKey_StoreFailed"];
            return;
        }
        AiApiKeyError = null;
        AiApiKeyMasked = null;
        if (AiEnabled && AiCliStatus is { IsChecking: false }) await AiCliStatus.RefreshAsync();
    }

    [RelayCommand]
    private static void OpenAnthropicConsole() => UrlOpener.Open(AiApiKeyStore.ConsoleUrl);

    partial void OnAiApiKeyDraftChanged(string value) => AiApiKeyError = null;

    [RelayCommand]
    private void UseClaudeSubscription() => AiClaudeUseApi = false;

    [RelayCommand]
    private void UseClaudeApiKey() => AiClaudeUseApi = true;

    [ObservableProperty]
    private string _aiCodexPath = string.Empty;
    [ObservableProperty]
    private string _aiLocalEndpoint = string.Empty;
    [ObservableProperty]
    private string _aiCustomModel = string.Empty;

    partial void OnAiProviderChanged(AiProviderOption? value)
    {
        if (_loadingAiSettings || value is null) return;
        if (AiCliStatus?.IsBusy == true) { LoadAiSettings(); return; }
        _settingsService.Settings.Ai.Provider = value.Provider;
        AiCliStatus?.Backend?.SelectProvider(value.Provider);
        LoadAiSettings();
        _settingsService.Save();
        if (AiEnabled && AiCliStatus is { IsChecking: false }) _ = AiCliStatus.RefreshAsync();
    }

    partial void OnAiCodexPathChanged(string value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.CodexCliPath = value.Trim();
        DebouncedSave();
    }

    partial void OnAiLocalEndpointChanged(string value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.OllamaEndpoint = value.Trim().TrimEnd('/');
        DebouncedSave();
    }

    partial void OnAiCustomModelChanged(string value)
    {
        if (_loadingAiSettings || AiProvider is null) return;
        _settingsService.Settings.Ai.SetModel(AiProvider.Provider, value.Trim());
        DebouncedSave();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiStatusVisible))]
    [NotifyCanExecuteChangedFor(nameof(CheckClaudeCliCommand))]
    private bool _aiEnabled;

    [ObservableProperty]
    private bool _isAiConsentDialogOpen;

    [ObservableProperty]
    private string _aiCliPath = string.Empty;

    [ObservableProperty]
    private AiModelOption? _aiDefaultModel;

    [ObservableProperty]
    private int _aiIdleTimeoutSeconds;

    [ObservableProperty]
    private int _aiMaxContextChars;

    [ObservableProperty]
    private bool _aiConfirmBeforeSending;

    [ObservableProperty]
    private bool _aiAllowVaultChanges;

    [ObservableProperty]
    private bool _aiAllowReadAccessByDefault;

    [ObservableProperty]
    private bool _isClearAiHistoryConfirmOpen;

    /// <summary>La tarjeta de estado del CLI solo se enseña con la IA encendida: apagada, Enki no ejecuta
    /// nada, ni siquiera la comprobación.</summary>
    public bool AiStatusVisible => AiEnabled && AiCliStatus is not null;

    public string AiHistoryCountLabel => _aiChatService is null
        ? string.Empty
        : Plural.Get("Settings_Ai_History_Count", _aiChatService.Sessions.Count, _aiChatService.Sessions.Count);

    public bool HasAiHistory => _aiChatService?.Sessions.Count > 0;

    /// <summary>Evita que cargar los valores guardados se interprete como una edición del usuario.</summary>
    private bool _loadingAiSettings;

    private void InitializeAi(ClaudeCliStatusViewModel? cliStatus, AiChatService? chatService)
    {
        AiCliStatus = cliStatus;
        if (cliStatus is not null)
            cliStatus.PropertyChanged += (_, _) => CheckClaudeCliCommand.NotifyCanExecuteChanged();
        if (cliStatus?.Backend is { } backend)
        {
            backend.ProviderChanged += () =>
            {
                _settingsService.Settings.Ai.Provider = backend.Provider;
                LoadAiSettings();
            };
            cliStatus.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ClaudeCliStatusViewModel.Status)) return;
                LoadAiModelOptions();
            };
        }
        _aiChatService = chatService;
        if (chatService is not null)
            chatService.ChatsChanged += () =>
            {
                OnPropertyChanged(nameof(AiHistoryCountLabel));
                OnPropertyChanged(nameof(HasAiHistory));
            };
        LoadAiSettings();
    }

    private void LoadAiSettings()
    {
        _loadingAiSettings = true;
        try
        {
            var ai = _settingsService.Settings.Ai;
            AiProvider = AiProviderOptions.First(p => p.Provider == ai.Provider);
            AiClaudeUseApi = ai.ClaudeUseApi;
            LoadAiApiKeyStatus();
            AiCodexPath = ai.CodexCliPath;
            AiLocalEndpoint = ai.OllamaEndpoint;
            AiCustomModel = ai.ModelFor(ai.Provider);
            LoadAiModelOptions();
            AiEnabled = ai.Enabled;
            AiCliPath = ai.ClaudeCliPath;
            AiIdleTimeoutSeconds = ai.IdleTimeoutSeconds;
            AiMaxContextChars = ai.MaxContextChars;
            AiConfirmBeforeSending = ai.ConfirmBeforeSendingContext;
            AiAllowVaultChanges = ai.AllowVaultChanges;
            AiAllowReadAccessByDefault = ai.AllowReadAccessByDefault;
        }
        finally
        {
            _loadingAiSettings = false;
        }
    }

    private void LoadAiModelOptions()
    {
        var wasLoading = _loadingAiSettings;
        _loadingAiSettings = true;
        try
        {
            var ai = _settingsService.Settings.Ai;
            AiModelOptions.Clear();
            foreach (var option in AiModelOption.Build(ai.Provider, AiCliStatus?.Backend?.LocalModels, ai.ModelFor(ai.Provider)))
                AiModelOptions.Add(option);
            AiDefaultModel = AiModelOptions.FirstOrDefault(m => m.Alias == ai.ModelFor(ai.Provider)) ?? AiModelOptions[0];
        }
        finally { _loadingAiSettings = wasLoading; }
    }

    partial void OnAiEnabledChanged(bool value)
    {
        if (_loadingAiSettings) return;
        var ai = _settingsService.Settings.Ai;

        // Primera activación: nada se enciende hasta aceptar el aviso. El interruptor vuelve a su sitio y el
        // diálogo decide.
        if (value && ai.ConsentAcceptedUtc is null)
        {
            SetAiEnabledSilently(false);
            IsAiConsentDialogOpen = true;
            return;
        }
        if (BlockIfLastEnabledSidebarSection("Assistant", value, () => SetAiEnabledSilently(true))) return;

        ai.Enabled = value;
        _settingsService.Save();
        _onSidebarSectionChanged(AppPage.Assistant, value);
        SyncSidebarSectionRow("Assistant", value);
        if (value && AiCliStatus is { IsChecking: false }) _ = AiCliStatus.RefreshAsync();
    }

    private void SetAiEnabledSilently(bool value)
    {
        _loadingAiSettings = true;
        try
        {
            AiEnabled = value;
        }
        finally
        {
            _loadingAiSettings = false;
        }
        SyncSidebarSectionRow("Assistant", value);
    }

    [RelayCommand]
    private void AcceptAiConsent()
    {
        _settingsService.Settings.Ai.ConsentAcceptedUtc = DateTime.UtcNow;
        IsAiConsentDialogOpen = false;
        AiEnabled = true;
    }

    [RelayCommand]
    private void DeclineAiConsent() => IsAiConsentDialogOpen = false;

    partial void OnAiCliPathChanged(string value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.ClaudeCliPath = value.Trim();
        DebouncedSave();
    }

    [RelayCommand]
    private async Task BrowseClaudeCliPath()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_Ai_Cli_PickerTitle"],
            AllowMultiple = false
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(path)) return;
        AiCliPath = path;
        await CheckClaudeCli();
    }

    [RelayCommand]
    private async Task ClearClaudeCliPath()
    {
        AiCliPath = string.Empty;
        await CheckClaudeCli();
    }

    private bool CanCheckAiConnection => AiEnabled && AiCliStatus is { IsChecking: false, IsBusy: false };

    /// <summary>Guarda ya la configuración (sin esperar al debounce) y comprueba el proveedor seleccionado.</summary>
    [RelayCommand(CanExecute = nameof(CanCheckAiConnection))]
    private async Task CheckClaudeCli()
    {
        FlushPendingSave();
        if (AiCliStatus is not null) await AiCliStatus.RefreshAsync();
    }

    partial void OnAiDefaultModelChanged(AiModelOption? value)
    {
        if (_loadingAiSettings || value is null) return;
        _settingsService.Settings.Ai.SetModel(_settingsService.Settings.Ai.Provider, value.Alias);
        _loadingAiSettings = true;
        AiCustomModel = value.Alias;
        _loadingAiSettings = false;
        _settingsService.Save();
    }

    partial void OnAiIdleTimeoutSecondsChanged(int value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.IdleTimeoutSeconds = Math.Clamp(value, AiSettings.MinIdleTimeoutSeconds, AiSettings.MaxIdleTimeoutSeconds);
        DebouncedSave();
    }

    partial void OnAiMaxContextCharsChanged(int value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.MaxContextChars = Math.Clamp(value, AiSettings.MinMaxContextChars, AiSettings.MaxMaxContextChars);
        DebouncedSave();
    }

    partial void OnAiConfirmBeforeSendingChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.ConfirmBeforeSendingContext = value;
        _settingsService.Save();
    }

    /// <summary>Se aplica en caliente sin convertir el permiso heredado en un permiso permanente del chat.</summary>
    partial void OnAiAllowReadAccessByDefaultChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.AllowReadAccessByDefault = value;
        _settingsService.Save();
    }

    /// <summary>Se aplica en caliente: el servidor del vault lo lee en cada llamada, así que apagarlo a mitad
    /// de una respuesta ya impide la siguiente acción que cambie algo.</summary>
    partial void OnAiAllowVaultChangesChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.AllowVaultChanges = value;
        _settingsService.Save();
    }

    [RelayCommand]
    private void OpenAiTerms() => UrlOpener.Open(AiCliStatus?.TermsUrl ?? ClaudeCliService.LegalAndComplianceUrl);

    [RelayCommand]
    private static void OpenAnthropicConsumerTerms() => UrlOpener.Open(ClaudeCliService.ConsumerTermsUrl);

    [RelayCommand]
    private void OpenClaudeSetupDocumentation() => UrlOpener.Open(AiCliStatus?.SetupDocumentationUrl ?? ClaudeCliService.SetupDocumentationUrl);

    [RelayCommand]
    private void RequestClearAiHistory()
    {
        if (HasAiHistory) IsClearAiHistoryConfirmOpen = true;
    }

    [RelayCommand]
    private void CancelClearAiHistory() => IsClearAiHistoryConfirmOpen = false;

    [RelayCommand]
    private void ConfirmClearAiHistory()
    {
        IsClearAiHistoryConfirmOpen = false;
        if (_aiChatService is null) return;
        try
        {
            _aiChatService.DeleteAll();
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Ai_History_Cleared_Title"),
                LocalizedTextFormatter.Format("Settings_Ai_History_Cleared_Message"), NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notificationService.PushLocalized(LocalizedTextFormatter.Format("Assistant_Notify_DeleteFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
    }

    private void RefreshAiLocalizedLabels()
    {
        LoadAiSettings();
        OnPropertyChanged(nameof(AiApiKeyStatus));
        AiCliStatus?.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(AiHistoryCountLabel));
    }
}
