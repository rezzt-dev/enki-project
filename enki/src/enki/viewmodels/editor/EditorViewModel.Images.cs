using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>
/// Todo lo que la pestaña del editor necesita saber sobre las imágenes de su nota: dónde vive la nota
/// (el ancla de cualquier ruta relativa), cómo leer un archivo del vault descifrándolo si hace falta, y
/// cómo adjuntar una imagen recién pegada, arrastrada o elegida.
///
/// <para>Está aquí y no en <c>EditorView</c> por la misma razón que el resto del MVVM del proyecto: la
/// vista sabe dibujar una imagen y capturar un <c>Ctrl+V</c>, pero quien habla con <c>VaultService</c>
/// —y por tanto quien decide qué se escribe en el vault y qué se hace cuando falla— es el viewmodel.
/// Ver docs/imagenes-en-notas.md.</para>
/// </summary>
public sealed partial class EditorViewModel
{
    private NoteImageService? _imageService;

    /// <summary>Creado a demanda en vez de en el constructor porque la inmensa mayoría de pestañas nunca
    /// adjuntan una imagen — y porque el constructor de <see cref="EditorViewModel"/> ya recibe todo lo
    /// que hace falta para construirlo aquí, sin sumarle otro parámetro a la composición manual de
    /// <c>MainWindowViewModel</c>.</summary>
    private NoteImageService ImageService => _imageService ??= new NoteImageService(_vaultService);

    /// <summary>Carpeta que contiene la nota abierta: contra ella se resuelve cada <c>![](ruta)</c>
    /// relativo, y dentro de ella se crea la carpeta de adjuntos.</summary>
    public string? NoteDirectory => Path.GetDirectoryName(Note.FullPath);

    /// <summary>Raíz del vault — el último recurso para localizar por nombre el destino de un
    /// <c>![[archivo.png]]</c>, que por convención se escribe sin ruta.</summary>
    public string VaultRootPath => _vaultService.VaultPath;

    /// <summary>Lee los bytes en claro de un archivo del vault. Es la única vía por la que el live preview
    /// carga una imagen: pasar por aquí es lo que hace que una imagen adjunta a una nota de una sesión
    /// protegida se descifre sola al dibujarla, y que una sesión bloqueada devuelva un error en vez de
    /// bytes cifrados interpretados como si fueran un PNG.</summary>
    public byte[] ReadVaultFileBytes(string fullPath) => _vaultService.ReadFileBytes(fullPath);

    /// <summary>Guarda como adjunto de esta nota una imagen que llega ya codificada (lo que sale del
    /// portapapeles, siempre PNG) y devuelve la ruta relativa lista para escribir dentro de un
    /// <c>![](…)</c>, o null si no se pudo guardar — en cuyo caso el usuario ya ha visto por qué.</summary>
    public string? AttachImageBytes(byte[] data, string extension) =>
        Attach(() => ImageService.SaveBytes(Note, data, extension));

    /// <summary>Adjunta a esta nota un archivo de imagen ya existente en disco (arrastrado desde el
    /// explorador, elegido en el diálogo). Un archivo que ya está dentro del vault se enlaza donde está;
    /// cualquier otro se copia a la carpeta de adjuntos de la nota. Ver
    /// <see cref="NoteImageService.SaveFromFile"/>.</summary>
    public string? AttachImageFile(string sourcePath) =>
        Attach(() => ImageService.SaveFromFile(Note, sourcePath));

    /// <summary>Abre una imagen de la nota con el visor del sistema — lo que hace Ctrl+clic sobre ella,
    /// el mismo gesto que sobre un enlace lo abre en el navegador. Una imagen que vive en una sesión
    /// protegida está cifrada en disco y ningún visor sabría qué hacer con ella, así que se le entrega
    /// una copia descifrada temporal con el mismo trato (y la misma limpieza diferida, best-effort) que
    /// <c>FilesViewModel.OpenWithSystem</c>.</summary>
    public void OpenImageExternally(string fullPath)
    {
        var readablePath = fullPath;
        var isTempCopy = false;
        try
        {
            readablePath = _vaultService.ResolveReadablePath(fullPath);
            isTempCopy = !VaultService.PathsEqual(readablePath, fullPath);

            Process.Start(new ProcessStartInfo(readablePath) { UseShellExecute = true });

            if (isTempCopy)
            {
                var tempPath = readablePath;
                _ = Task.Delay(TimeSpan.FromMinutes(5))
                    .ContinueWith(_ => VaultService.TryCleanupDecryptedTempFile(tempPath), TaskScheduler.Default);
            }
        }
        catch (Exception ex)
        {
            if (isTempCopy) VaultService.TryCleanupDecryptedTempFile(readablePath);
            LogService.Warning($"No se pudo abrir la imagen '{fullPath}' con el visor del sistema", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NoteImageOpenFailed_Title"),
                LocalizedTextFormatter.Format("Notification_NoteImageOpenFailed_Message", Path.GetFileName(fullPath)),
                NotificationSeverity.Warning);
        }
    }

    /// <summary>Envoltorio común de las dos formas de adjuntar: un fallo de disco, de permisos o de sesión
    /// bloqueada no puede tragarse en silencio dejando al usuario con la impresión de que la imagen se
    /// pegó, así que se registra y se anuncia con el mismo mecanismo que el resto del editor
    /// (<see cref="NotificationService"/>), y se devuelve null para que la vista no inserte nada.</summary>
    private string? Attach(Func<string?> save)
    {
        try
        {
            return save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException
                                       or SessionLockedException)
        {
            LogService.Error($"No se pudo adjuntar una imagen a la nota '{Note.FullPath}'", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NoteImageAttachFailed_Title"),
                LocalizedTextFormatter.Format("Notification_NoteImageAttachFailed_Message", VaultFailureReasons.DescribeLocalized(ex)),
                NotificationSeverity.Error);
            return null;
        }
    }
}
