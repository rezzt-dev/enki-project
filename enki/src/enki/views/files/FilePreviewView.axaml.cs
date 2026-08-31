using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Code-behind de la previsualización: instalar TextMate sobre el visor de código y disparar el
/// renderizado perezoso de las miniaturas del rail del visor de PDF. Vive aquí y no en las disposiciones
/// porque las dos montan este mismo control, y porque es justo lo que no debe existir por duplicado: una
/// instalación de TextMate y un PDFium por cada vista viva.</summary>
public partial class FilePreviewView : UserControl
{
    private TextMate.Installation? _codeTextMateInstallation;
    private RegistryOptions? _codeRegistryOptions;
    private FileCardViewModel? _currentCodeCard;

    public FilePreviewView()
    {
        InitializeComponent();
        CodeEditor.Initialized += CodeEditor_Initialized;
        CodeEditor.DataContextChanged += CodeEditor_DataContextChanged;
    }

    /// <summary>Instala TextMate sobre el editor de código la primera vez que se crea su control
    /// nativo. Las gramáticas se cargan una sola vez (RegistryOptions es costoso de construir) y
    /// la instalación de TextMate se reutiliza mientras esta vista viva. Tema fijo DarkPlus:
    /// el diseño brutalista monocromo de Enki usa fondos oscuros también en modo claro, así que
    /// el contraste de DarkPlus funciona en ambos.</summary>
    private void CodeEditor_Initialized(object? sender, EventArgs e)
    {
        if (_codeRegistryOptions is not null) return;

        _codeRegistryOptions = new RegistryOptions(ThemeName.DarkPlus);
        _codeTextMateInstallation = CodeEditor.InstallTextMate(_codeRegistryOptions);

        // Aplicar explícitamente el tema DarkPlus sobre el editor — sin esto, el foreground y
        // background del tema no se aplican y el editor se queda con los defaults de Avalonia.
        _codeTextMateInstallation.SetTheme(_codeRegistryOptions.LoadTheme(ThemeName.DarkPlus));
    }

    /// <summary>Cuando el DataContext del editor de código cambia (el usuario abre otro archivo),
    /// actualiza la gramática de TextMate al lenguaje correspondiente usando
    /// <c>GetLanguageByExtension</c> → <c>GetScopeByLanguageId</c> como indica la documentación
    /// oficial de AvaloniaEdit.TextMate.</summary>
    private void CodeEditor_DataContextChanged(object? sender, EventArgs e)
    {
        if (CodeEditor.DataContext is not FileCardViewModel card) return;

        // Si ya tenemos la misma card, no reinstalar la gramática
        if (ReferenceEquals(card, _currentCodeCard)) return;
        _currentCodeCard = card;

        if (_codeTextMateInstallation is null || _codeRegistryOptions is null) return;
        if (card.CodeLanguage is null) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                // API oficial de AvaloniaEdit.TextMate:
                // 1. GetLanguageByExtension devuelve el ILanguage (contiene Id interno)
                // 2. GetScopeByLanguageId(language.Id) devuelve el scope name
                // 3. SetGrammar(scopeName) aplica el resaltado de sintaxis
                var language = _codeRegistryOptions.GetLanguageByExtension(card.CodeLanguage);
                if (language is null) return;
                var scopeName = _codeRegistryOptions.GetScopeByLanguageId(language.Id);
                if (scopeName is not null)
                    _codeTextMateInstallation.SetGrammar(scopeName);
            }
            catch
            {
                // Lenguaje no soportado por TextMate — se queda con la gramática anterior o sin resaltado
            }
        }, DispatcherPriority.Loaded);
    }

    // ===================== PDF page rail (lazy thumbnails) =====================

    private void PdfPageThumb_Initialized(object? sender, EventArgs e)
    {
        if (sender is not Control { DataContext: PdfPageThumbnailViewModel page } control) return;
        if (control.FindAncestorOfType<ItemsControl>()?.DataContext is PdfDocumentViewModel document)
            document.EnsurePageThumbnailLoaded(page);
    }
}
