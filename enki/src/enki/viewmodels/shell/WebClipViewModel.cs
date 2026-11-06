using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Pegar una URL y que salga una nota (T3, .docs/project-info/web-clipper.md). Es el popup del
/// shell, hermano de la paleta de comandos y del quick switcher: un campo, un botón y sitio para contar
/// qué ha pasado.
///
/// <para>La nota se crea en un solo punto, <see cref="Save"/>, y siempre: si la extracción sale completa,
/// con el texto; y si la página no se deja leer, con el título y el enlace. Lo único que <b>no</b> crea
/// nota es cancelar, que es el criterio de aceptación de esta funcionalidad.</para></summary>
public sealed partial class WebClipViewModel : ObservableObject
{
    private readonly WebClipService _webClipService;
    private readonly VaultService _vaultService;
    private readonly NotificationService _notificationService;
    private readonly Action<NoteFile> _onNoteCreated;

    /// <summary>Token de la captura en curso. Se cancela desde el botón "Cancelar" y también al cerrar el
    /// popup, para que una página lenta no siga descargándose detrás de una ventana que ya no está.</summary>
    private CancellationTokenSource? _inFlight;

    public WebClipViewModel(WebClipService webClipService, VaultService vaultService,
        NotificationService notificationService, Action<NoteFile> onNoteCreated)
    {
        _webClipService = webClipService;
        _vaultService = vaultService;
        _notificationService = notificationService;
        _onNoteCreated = onNoteCreated;
    }

    [ObservableProperty]
    private bool _isOpen;

    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [ObservableProperty]
    private string _url = string.Empty;

    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Qué ha ido mal, ya traducido y listo para pintarse bajo el campo. Cadena vacía = nada que
    /// contar. Vive aquí y no solo en una notificación porque el error de una URL mal escrita hay que
    /// leerlo <em>junto al campo que hay que corregir</em>.</summary>
    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public bool IsIdle => !IsBusy;

    private bool CanSave => !IsBusy && Url.Trim().Length > 0;

    /// <summary>Abre el popup, opcionalmente con una URL ya puesta — la trae la captura rápida (T2) cuando
    /// lo que el usuario escribió resulta ser un enlace.</summary>
    public void Open(string? initialUrl = null)
    {
        if (!string.IsNullOrWhiteSpace(initialUrl)) Url = initialUrl.Trim();
        ErrorMessage = string.Empty;
        IsOpen = true;
    }

    /// <summary>Versión-comando de <see cref="Open(string?)"/> sin URL inicial, para los botones que lo
    /// abren desde XAML (la cabecera de Documentos) y para la paleta de comandos.</summary>
    [RelayCommand]
    private void Show() => Open();

    partial void OnIsOpenChanged(bool value)
    {
        if (value) return;

        // Cerrar equivale a cancelar: nada sigue descargándose detrás de un popup que ya no se ve.
        _inFlight?.Cancel();
        ErrorMessage = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        ErrorMessage = string.Empty;

        if (!_vaultService.IsVaultConfigured)
        {
            ErrorMessage = LocalizationService.Instance["Notification_VaultUnavailable_Reason"];
            return;
        }
        if (!WebClipService.TryParseUrl(Url, out var url, out var rejection))
        {
            ErrorMessage = LocalizedTextFormatter.Resolve(rejection);
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;
        IsBusy = true;
        try
        {
            var result = await _webClipService.ClipAsync(url, cancellation.Token);
            var note = _webClipService.SaveNote(result);

            IsOpen = false;
            Url = string.Empty;
            _onNoteCreated(note);

            if (result.ExtractionComplete)
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Notification_WebClipSaved_Title"),
                    LocalizedTextFormatter.Format("Notification_WebClipSaved_Message", note.Title),
                    NotificationSeverity.Success);
            else
                // La nota existe: el aviso cuenta que le falta el texto, no que haya fallado nada.
                _notificationService.PushLocalized(
                    LocalizedTextFormatter.Format("Notification_WebClipPartial_Title"),
                    LocalizedTextFormatter.Format("Notification_WebClipPartial_Message", note.Title,
                        result.PartialReason ?? LocalizedTextFormatter.Format("WebClip_Partial_Unreachable")),
                    NotificationSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            // Cancelación del usuario: ni nota ni aviso, se queda el popup como estaba.
        }
        catch (WebClipRejectedException ex)
        {
            ErrorMessage = LocalizedTextFormatter.Resolve(ex.Reason);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
                                       or VaultUnavailableException or SessionLockedException)
        {
            // La descarga fue bien y lo que falló fue escribir la nota: el motivo se describe con el mismo
            // vocabulario que cualquier otro fallo de escritura sobre el vault.
            ErrorMessage = VaultFailureReasons.Describe(ex);
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_WebClipFailed_Title"),
                VaultFailureReasons.DescribeLocalized(ex), NotificationSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            _inFlight = null;
        }
    }

    /// <summary>Cancela la captura en curso; con ninguna en curso, cierra el popup. Un solo botón para los
    /// dos casos porque desde fuera es la misma intención: "déjalo".</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy) _inFlight?.Cancel();
        else IsOpen = false;
    }
}
