using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using enki.Views.Editor.Markdown;

namespace enki.Views;

/// <summary>
/// Las cuatro formas de meter una imagen en una nota — pegar del portapapeles, arrastrar desde el
/// explorador, el botón de la barra de formato y el comando <c>/imagen</c> — más el Ctrl+clic que la
/// abre en el visor del sistema. Las cuatro terminan en el mismo sitio:
/// <see cref="EditorViewModel.AttachImageBytes"/> / <see cref="EditorViewModel.AttachImageFile"/>
/// guardan el adjunto y devuelven la ruta relativa, y <see cref="InsertImageReferences"/> la escribe en
/// el documento. Ver docs/imagenes-en-notas.md.
/// </summary>
public partial class EditorView
{
    /// <summary>Extensiones que el diálogo de "insertar imagen" ofrece — las mismas que el editor sabe
    /// dibujar (<see cref="NoteImageAttachments.IsImageFile"/>).</summary>
    private static readonly string[] ImagePickerPatterns =
        ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.svg", "*.bmp", "*.avif", "*.ico", "*.tif", "*.tiff"];

    /// <summary>Conecta el editor como destino de arrastre de archivos. Los manejadores van en la fase de
    /// captura (<see cref="RoutingStrategies.Tunnel"/>) para llegar antes que el arrastre de texto propio
    /// de AvaloniaEdit, y solo reclaman el evento cuando lo que viene son imágenes: arrastrar texto dentro
    /// de la nota tiene que seguir funcionando igual que siempre.</summary>
    private void InitializeImageDropTarget()
    {
        DragDrop.SetAllowDrop(Editor, true);
        Editor.AddHandler(DragDrop.DragOverEvent, OnEditorDragOver, RoutingStrategies.Tunnel);
        Editor.AddHandler(DragDrop.DropEvent, OnEditorDrop, RoutingStrategies.Tunnel);
    }

    /// <summary>Repone en el estado del live preview de dónde salen las imágenes de la nota que se acaba
    /// de abrir. Sin esto, el generador de elementos no tendría contra qué resolver una ruta relativa y
    /// toda imagen aparecería como rota al cambiar de pestaña.</summary>
    private void ApplyImageContext(EditorViewModel? vm)
    {
        _livePreviewState.Images = vm is null
            ? MarkdownImageContext.Empty
            : new MarkdownImageContext
            {
                NoteDirectory = vm.NoteDirectory,
                VaultPath = vm.VaultRootPath,
                ReadBytes = vm.ReadVaultFileBytes
            };
    }

    // ===================== Pegar =====================

    /// <summary>Cierto si <paramref name="e"/> es el gesto de pegar de la plataforma (Ctrl+V, o Cmd+V en
    /// macOS). Se compara contra el gesto que declara la propia plataforma en vez de contra
    /// <c>KeyModifiers.Control</c> a pelo, para no romper el pegado en macOS.</summary>
    private static bool IsPasteGesture(KeyEventArgs e)
    {
        var hotkeys = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys is null) return e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control;

        foreach (var gesture in hotkeys.Paste)
        {
            if (gesture.Key == e.Key && gesture.KeyModifiers == e.KeyModifiers) return true;
        }
        return false;
    }

    /// <summary>Pega del portapapeles. Se hace a mano en vez de dejárselo a AvaloniaEdit porque leer el
    /// portapapeles es asíncrono y hay que mirar qué trae antes de decidir: si son archivos de imagen o
    /// una captura, se adjuntan a la nota; en cualquier otro caso se delega en el pegado de siempre, que
    /// es exactamente lo que hacía antes de esta función.</summary>
    private async Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            try
            {
                if (await TryPasteImageAsync(clipboard)) return;
            }
            catch (Exception ex)
            {
                // Un portapapeles inaccesible (ventana sin foco en algunos gestores de X11, sesión
                // remota) no puede impedir pegar texto: se anota y se sigue por la vía normal.
                LogService.Warning("No se pudo leer una imagen del portapapeles", ex);
            }
        }

        Editor.Paste();
    }

    /// <summary>Devuelve si el contenido del portapapeles se ha insertado como imagen. El orden importa:
    /// <list type="number">
    /// <item>archivos de imagen primero — copiar una imagen en el explorador deja en el portapapeles el
    /// archivo <em>y</em> su ruta como texto, y lo que el usuario quiere es la imagen, no la ruta;</item>
    /// <item>un mapa de bits solo si no viene también texto — al copiar de un navegador o de un editor de
    /// texto enriquecido suelen viajar ambos, y ahí lo que se está pegando es texto.</item>
    /// </list></summary>
    private async Task<bool> TryPasteImageAsync(IClipboard clipboard)
    {
        using var data = await clipboard.TryGetDataAsync();
        if (data is null) return false;

        var files = await data.TryGetFilesAsync();
        if (files is { Length: > 0 })
        {
            var paths = LocalImagePaths(files);
            if (paths.Count > 0)
            {
                InsertImageReferences(AttachAll(paths), Editor.CaretOffset);
                return true;
            }
        }

        if (data.Contains(DataFormat.Text)) return false;

        var bitmap = await data.TryGetBitmapAsync();
        if (bitmap is null) return false;

        if (DataContext is not EditorViewModel vm) return false;

        using var stream = new MemoryStream();
        bitmap.Save(stream, new PngBitmapEncoderOptions());
        var reference = vm.AttachImageBytes(stream.ToArray(), ".png");
        if (reference is null) return true; // Falló el guardado; el usuario ya tiene el aviso.

        InsertImageReferences([reference], Editor.CaretOffset);
        return true;
    }

    // ===================== Arrastrar y soltar =====================

    private void OnEditorDragOver(object? sender, DragEventArgs e)
    {
        if (!CarriesImages(e)) return;
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnEditorDrop(object? sender, DragEventArgs e)
    {
        if (!CarriesImages(e)) return;
        e.Handled = true;

        var files = e.DataTransfer.TryGetFiles();
        if (files is null) return;

        var paths = LocalImagePaths(files);
        if (paths.Count == 0) return;

        // La imagen se inserta donde se soltó, no donde estaba el cursor: soltar sobre un párrafo
        // concreto y que la imagen aparezca en otro sitio de la nota sería desconcertante.
        InsertImageReferences(AttachAll(paths), OffsetAt(e.GetPosition(Editor.TextArea.TextView)));
    }

    private static bool CarriesImages(DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File)) return false;
        var files = e.DataTransfer.TryGetFiles();
        return files is not null && LocalImagePaths(files).Count > 0;
    }

    private static List<string> LocalImagePaths(IEnumerable<IStorageItem> items) => items
        .Select(item => item.TryGetLocalPath())
        .Where(path => path is not null && NoteImageAttachments.IsImageFile(path))
        .Select(path => path!)
        .ToList();

    /// <summary>Desplazamiento del documento bajo un punto de la vista de texto; si el punto cae fuera de
    /// cualquier línea (por debajo del final del documento, por ejemplo) se usa el final del texto.</summary>
    private int OffsetAt(Avalonia.Point point)
    {
        var position = Editor.TextArea.TextView.GetPosition(point);
        return position is null
            ? Editor.Document.TextLength
            : Editor.Document.GetOffset(position.Value.Location);
    }

    // ===================== Barra de formato y /imagen =====================

    /// <summary>Abre el diálogo del sistema para elegir una o varias imágenes y las adjunta a la nota. Es
    /// lo que hacen tanto el botón de imagen de la barra de formato como el comando <c>/imagen</c>.</summary>
    private async Task PickAndInsertImagesAsync(int offset)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null) return;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Instance["Editor_ImagePicker_DialogTitle"],
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(LocalizationService.Instance["Editor_ImagePicker_FileTypeLabel"])
                {
                    Patterns = ImagePickerPatterns
                }
            ]
        });

        var paths = LocalImagePaths(files);
        if (paths.Count == 0) return;

        InsertImageReferences(AttachAll(paths), Math.Clamp(offset, 0, Editor.Document.TextLength));
    }

    // ===================== Inserción =====================

    private List<string> AttachAll(IEnumerable<string> sourcePaths)
    {
        var references = new List<string>();
        if (DataContext is not EditorViewModel vm) return references;

        foreach (var path in sourcePaths)
        {
            if (vm.AttachImageFile(path) is { } reference) references.Add(reference);
        }
        return references;
    }

    /// <summary>Escribe cada imagen en su propia línea, reutilizando el salto existente y sin añadir
    /// una línea vacía al final. El cursor puede quedar al final del span sin ocultar la imagen.</summary>
    private void InsertImageReferences(List<string> references, int offset)
    {
        if (references.Count == 0) return;

        var document = Editor.Document;
        var start = Math.Clamp(offset, 0, document.TextLength);
        var line = document.GetLineByOffset(start);
        var needsLeadingBreak = start > line.Offset;
        var needsTrailingBreak = start < line.EndOffset;
        var newline = line.DelimiterLength > 0
            ? document.GetText(line.EndOffset, line.DelimiterLength)
            : "\n";

        var text = (needsLeadingBreak ? newline : string.Empty)
                   + string.Join(newline, references.Select(reference => $"![]({reference})"))
                   + (needsTrailingBreak ? newline : string.Empty);

        document.Insert(start, text);
        Editor.CaretOffset = start + text.Length;
        Editor.Focus();
    }

    // ===================== Ctrl+clic =====================

    /// <summary>Abre la imagen sobre la que se acaba de hacer Ctrl+clic: con el visor del sistema si es un
    /// archivo del vault, y en el navegador si la nota la referencia por URL. Devuelve si había algo que
    /// abrir.</summary>
    private bool TryOpenImage(MdSpan span)
    {
        if (DataContext is not EditorViewModel vm || span.LinkTarget is not { } src) return false;

        var target = MarkdownImageResolver.Resolve(_livePreviewState.Images, src);
        switch (target.Kind)
        {
            case MarkdownImageKind.File when File.Exists(target.Value):
                vm.OpenImageExternally(target.Value);
                return true;
            case MarkdownImageKind.Remote:
                OpenExternalUrl(target.Value);
                return true;
            default:
                return false;
        }
    }
}
