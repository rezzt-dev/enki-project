using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Inteligencia artificial › Acceso a internet: si los chats nuevos nacen con la web
/// encendida, con qué buscador se busca y, para Brave Search, la clave del usuario en el almacén seguro. Cada
/// chat lo cambia después con el botón del globo del compositor. Ver <c>.docs/project-info/asistente-ia.md</c>,
/// "Acceso a internet".</summary>
public sealed partial class SettingsViewModel
{
    private AiWebService? _aiWebService;

    /// <summary>Sin servicio web (pruebas que no componen el asistente) la tarjeta no se enseña.</summary>
    public bool ShowAiWebSettings => _aiWebService is not null;

    [ObservableProperty]
    private bool _aiWebAccessForNewChats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAiWebDuckDuckGo), nameof(IsAiWebBrave))]
    private AiWebSearchEngine _aiWebSearchEngine;

    public bool IsAiWebDuckDuckGo => AiWebSearchEngine == Models.AiWebSearchEngine.DuckDuckGo;
    public bool IsAiWebBrave => AiWebSearchEngine == Models.AiWebSearchEngine.Brave;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAiWebSearchKeyCommand))]
    private string _aiWebSearchKeyDraft = string.Empty;

    [ObservableProperty]
    private string? _aiWebSearchKeyError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AiWebSearchKeyStatus), nameof(HasAiWebSearchKey))]
    [NotifyCanExecuteChangedFor(nameof(DeleteAiWebSearchKeyCommand))]
    private string? _aiWebSearchKeyMasked;

    public bool HasAiWebSearchKey => AiWebSearchKeyMasked is not null;

    public string AiWebSearchKeyStatus => AiWebSearchKeyMasked is { } masked
        ? string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance["Settings_Ai_ApiKey_Saved"], masked)
        : LocalizationService.Instance["Settings_Ai_Web_BraveKey_None"];

    private void InitializeAiWeb(AiWebService? webService)
    {
        _aiWebService = webService;
        var wasLoading = _loadingAiSettings;
        _loadingAiSettings = true;
        try
        {
            var ai = _settingsService.Settings.Ai;
            AiWebAccessForNewChats = ai.WebAccessForNewChats;
            AiWebSearchEngine = ai.WebSearchEngine;
        }
        finally
        {
            _loadingAiSettings = wasLoading;
        }
        LoadAiWebSearchKeyStatus();
    }

    partial void OnAiWebAccessForNewChatsChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.WebAccessForNewChats = value;
        _settingsService.Save();
    }

    partial void OnAiWebSearchEngineChanged(AiWebSearchEngine value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.WebSearchEngine = value;
        _settingsService.Save();
        AiWebSearchKeyDraft = string.Empty;
        AiWebSearchKeyError = null;
        LoadAiWebSearchKeyStatus();
    }

    [RelayCommand]
    private void UseDuckDuckGoSearch() => AiWebSearchEngine = Models.AiWebSearchEngine.DuckDuckGo;

    [RelayCommand]
    private void UseBraveSearch() => AiWebSearchEngine = Models.AiWebSearchEngine.Brave;

    /// <summary>Solo se pregunta al almacén seguro con Brave elegido: en macOS leer el Keychain puede pedir
    /// permiso, y quien busca con DuckDuckGo no debería ver ese aviso.</summary>
    private void LoadAiWebSearchKeyStatus()
    {
        var store = IsAiWebBrave ? _aiWebService?.BraveKeys : null;
        var key = store?.TryRead();
        AiWebSearchKeyMasked = key is null ? null : store!.MaskKey(key);
    }

    private bool CanSaveAiWebSearchKey => AiWebSearchKeyDraft.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSaveAiWebSearchKey))]
    private void SaveAiWebSearchKey()
    {
        if (_aiWebService?.BraveKeys is not { } store) return;
        var key = AiWebSearchKeyDraft.Trim();
        if (!store.IsWellFormed(key))
        {
            AiWebSearchKeyError = LocalizationService.Instance["Settings_Ai_Web_BraveKey_Invalid"];
            return;
        }
        try
        {
            store.Write(key);
        }
        catch (Exception ex) when (AiApiKeyStore.IsStoreFailure(ex))
        {
            LogService.Warning("No se pudo guardar la clave de Brave Search en el almacén seguro", ex);
            AiWebSearchKeyError = LocalizationService.Instance["Settings_Ai_ApiKey_StoreFailed"];
            return;
        }
        AiWebSearchKeyError = null;
        AiWebSearchKeyDraft = string.Empty;
        AiWebSearchKeyMasked = store.MaskKey(key);
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Ai_ApiKey_SavedTitle"),
            LocalizedTextFormatter.Format("Settings_Ai_Web_BraveKey_SavedMessage"), NotificationSeverity.Success);
    }

    [RelayCommand(CanExecute = nameof(HasAiWebSearchKey))]
    private void DeleteAiWebSearchKey()
    {
        if (_aiWebService?.BraveKeys is not { } store) return;
        try
        {
            store.Delete();
        }
        catch (Exception ex) when (AiApiKeyStore.IsStoreFailure(ex))
        {
            LogService.Warning("No se pudo borrar la clave de Brave Search del almacén seguro", ex);
            AiWebSearchKeyError = LocalizationService.Instance["Settings_Ai_ApiKey_StoreFailed"];
            return;
        }
        AiWebSearchKeyError = null;
        AiWebSearchKeyMasked = null;
    }

    [RelayCommand]
    private static void OpenBraveSearchKeysPage() => UrlOpener.Open(AiApiKeyStore.BraveSearchKeysUrl);

    partial void OnAiWebSearchKeyDraftChanged(string value) => AiWebSearchKeyError = null;

    private void RefreshAiWebLocalizedLabels() => OnPropertyChanged(nameof(AiWebSearchKeyStatus));
}
