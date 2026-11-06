using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › General › "Buscar actualizaciones" (T9 de
/// .docs/funcionalidades-pendientes-priorizadas.md §4.5) — archivo parcial propio, mismo criterio que
/// <c>SettingsViewModel.Notifications.cs</c>.
///
/// <para>Aquí viven el interruptor de la comprobación al arrancar, el botón "Buscar ahora" y, cuando hay
/// una versión nueva, sus notas con los botones "Descargar" y "Omitir esta versión". El documento de la
/// funcionalidad también mencionaba "Recordármelo más tarde"; no se ha añadido como botón porque es
/// exactamente lo que ya pasa si no se pulsa nada —el aviso vuelve en la siguiente comprobación—, y un
/// botón que no hace nada visible es ruido, no una función.</para></summary>
public sealed partial class SettingsViewModel
{
    /// <summary>El mismo <see cref="UpdateCheckService"/> que usa el shell para la comprobación del
    /// arranque, pasado por el constructor — así la app entera tiene un único <c>HttpClient</c>, y quien lo
    /// crea es también quien lo cierra (<c>MainWindowViewModel.Shutdown</c>). Null en los tests y en los
    /// UI-tests que construyen este viewmodel a mano, donde se crea uno perezosamente la primera vez que
    /// alguien pulsa "Buscar ahora" (es decir, nunca, salvo que el test lo ejercite a propósito).</summary>
    private UpdateCheckService? _updateCheckService;

    private UpdateCheckService UpdateChecker => _updateCheckService ??= new UpdateCheckService(_settingsService);

    [ObservableProperty]
    private bool _checkUpdatesOnStartup;

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    /// <summary>La release encontrada, o <see langword="null"/> mientras no haya ninguna que ofrecer.</summary>
    [ObservableProperty]
    private UpdateInfo? _availableUpdate;

    /// <summary>Resultado de la última comprobación hecha desde esta pantalla; se queda en
    /// <see langword="null"/> hasta que el usuario pulsa "Buscar ahora", para no mostrar un estado que
    /// nadie ha pedido.</summary>
    [ObservableProperty]
    private UpdateCheckOutcome? _lastUpdateCheckOutcome;

    public bool HasAvailableUpdate => AvailableUpdate is not null;

    public string InstalledVersionLabel =>
        string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Updates_InstalledFormat"], AppVersion);

    public string AvailableUpdateLabel => AvailableUpdate is { } update
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Updates_AvailableFormat"], update.Version)
        : string.Empty;

    public string AvailableUpdateNotes => AvailableUpdate?.Notes ?? string.Empty;

    public bool HasAvailableUpdateNotes => !string.IsNullOrWhiteSpace(AvailableUpdateNotes);

    /// <summary>Línea de estado bajo el botón. Vacía antes de la primera comprobación manual; con una
    /// versión nueva encontrada tampoco dice nada, porque entonces habla el bloque de la release.</summary>
    public string UpdateCheckStatusLabel => LastUpdateCheckOutcome switch
    {
        UpdateCheckOutcome.UpToDate => LocalizationService.Instance["Settings_Updates_UpToDate"],
        UpdateCheckOutcome.Failed => LocalizationService.Instance["Settings_Updates_Failed"],
        _ => string.Empty
    };

    public bool HasUpdateCheckStatus => !string.IsNullOrEmpty(UpdateCheckStatusLabel);

    partial void OnCheckUpdatesOnStartupChanged(bool value)
    {
        _settingsService.Settings.CheckUpdatesOnStartup = value;
        _settingsService.Save();
    }

    partial void OnAvailableUpdateChanged(UpdateInfo? value)
    {
        OnPropertyChanged(nameof(HasAvailableUpdate));
        OnPropertyChanged(nameof(AvailableUpdateLabel));
        OnPropertyChanged(nameof(AvailableUpdateNotes));
        OnPropertyChanged(nameof(HasAvailableUpdateNotes));
    }

    partial void OnLastUpdateCheckOutcomeChanged(UpdateCheckOutcome? value)
    {
        OnPropertyChanged(nameof(UpdateCheckStatusLabel));
        OnPropertyChanged(nameof(HasUpdateCheckStatus));
    }

    /// <summary>"Buscar ahora": sin ajuste ni límite de frecuencia que valgan, y con el resultado siempre
    /// visible — incluido el fallo, que en la comprobación del arranque se calla a propósito.</summary>
    [RelayCommand]
    private async Task CheckForUpdates()
    {
        if (IsCheckingForUpdates) return;

        IsCheckingForUpdates = true;
        try
        {
            // Omitir una versión y volver a pulsar "Buscar ahora" tiene que volver a encontrarla: la
            // omisión silencia el aviso automático, no la comprobación que el usuario pide a mano.
            var previouslySkipped = _settingsService.Settings.SkippedUpdateVersion;
            _settingsService.Settings.SkippedUpdateVersion = null;
            var result = await UpdateChecker.CheckAsync();
            if (result.Outcome != UpdateCheckOutcome.UpdateAvailable)
                _settingsService.Settings.SkippedUpdateVersion = previouslySkipped;
            _settingsService.Save();

            LastUpdateCheckOutcome = result.Outcome;
            AvailableUpdate = result.Update;
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    /// <summary>Abre la página de la release en el navegador. Nunca descarga ni ejecuta nada: el enlace ya
    /// lo validó <see cref="UpdateCheckService"/> contra el repositorio oficial y <see cref="UrlOpener"/>
    /// lo vuelve a filtrar.</summary>
    [RelayCommand]
    private void DownloadUpdate()
    {
        if (AvailableUpdate is { } update) UrlOpener.Open(update.Url);
    }

    /// <summary>"Omitir esta versión": no se vuelve a avisar de ella ni de ninguna anterior.</summary>
    [RelayCommand]
    private void SkipUpdate()
    {
        if (AvailableUpdate is not { } update) return;

        _settingsService.Settings.SkippedUpdateVersion = update.Version;
        _settingsService.Save();
        AvailableUpdate = null;
        LastUpdateCheckOutcome = UpdateCheckOutcome.UpToDate;
    }

    /// <summary>Siembra la sección con lo que el shell encontró al arrancar, para que abrir Ajustes tras
    /// ver el aviso de la campana enseñe ya la release en vez de obligar a buscarla otra vez.</summary>
    public void ShowAvailableUpdate(UpdateInfo update)
    {
        AvailableUpdate = update;
        LastUpdateCheckOutcome = UpdateCheckOutcome.UpdateAvailable;
    }
}
