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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiStatusVisible))]
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
            AiModelOptions.Clear();
            foreach (var option in AiModelOption.Build()) AiModelOptions.Add(option);
            AiEnabled = ai.Enabled;
            AiCliPath = ai.ClaudeCliPath;
            AiDefaultModel = AiModelOptions.FirstOrDefault(m => string.Equals(m.Alias, ai.DefaultModel, StringComparison.OrdinalIgnoreCase))
                ?? AiModelOptions[0];
            AiIdleTimeoutSeconds = ai.IdleTimeoutSeconds;
            AiMaxContextChars = ai.MaxContextChars;
            AiConfirmBeforeSending = ai.ConfirmBeforeSendingContext;
            AiAllowVaultChanges = ai.AllowVaultChanges;
        }
        finally
        {
            _loadingAiSettings = false;
        }
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

    /// <summary>Guarda ya la ruta (sin esperar al debounce) y vuelve a comprobar el CLI con ella.</summary>
    [RelayCommand]
    private async Task CheckClaudeCli()
    {
        FlushPendingSave();
        if (AiCliStatus is not null) await AiCliStatus.RefreshAsync();
    }

    partial void OnAiDefaultModelChanged(AiModelOption? value)
    {
        if (_loadingAiSettings || value is null) return;
        _settingsService.Settings.Ai.DefaultModel = value.Alias;
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

    /// <summary>Se aplica en caliente: el servidor del vault lo lee en cada llamada, así que apagarlo a mitad
    /// de una respuesta ya impide la siguiente acción que cambie algo.</summary>
    partial void OnAiAllowVaultChangesChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.AllowVaultChanges = value;
        _settingsService.Save();
    }

    [RelayCommand]
    private static void OpenAiTerms() => UrlOpener.Open(ClaudeCliService.LegalAndComplianceUrl);

    [RelayCommand]
    private static void OpenAnthropicConsumerTerms() => UrlOpener.Open(ClaudeCliService.ConsumerTermsUrl);

    [RelayCommand]
    private static void OpenClaudeSetupDocumentation() => UrlOpener.Open(ClaudeCliService.SetupDocumentationUrl);

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
        AiCliStatus?.RefreshLocalizedLabels();
        OnPropertyChanged(nameof(AiHistoryCountLabel));
    }
}
