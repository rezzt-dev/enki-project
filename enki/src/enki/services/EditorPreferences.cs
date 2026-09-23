using System;

namespace enki.Services;

/// <summary>Live-updating editor preferences: any open <c>EditorView</c> subscribes to
/// <see cref="Changed"/> so a setting changed on the Settings page is reflected immediately,
/// without needing to close/reopen the note.</summary>
public static class EditorPreferences
{
    public static event Action? Changed;

    private static double _fontSize = 14.5;
    public static double FontSize
    {
        get => _fontSize;
        set
        {
            if (_fontSize.Equals(value)) return;
            _fontSize = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Max width, in pixels, of the readable text column in edit and preview mode.</summary>
    private static double _columnWidth = 760;
    public static double ColumnWidth
    {
        get => _columnWidth;
        set
        {
            if (_columnWidth.Equals(value)) return;
            _columnWidth = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Either "Center" or "Left" — where the readable column sits within the available space.</summary>
    private static string _columnPosition = "Center";
    public static string ColumnPosition
    {
        get => _columnPosition;
        set
        {
            if (_columnPosition == value) return;
            _columnPosition = value;
            Changed?.Invoke();
        }
    }

    /// <summary>System font family name for the markdown editor. Null/empty means the bundled Space Grotesk.</summary>
    private static string? _fontFamily;
    public static string? FontFamily
    {
        get => _fontFamily;
        set
        {
            if (_fontFamily == value) return;
            _fontFamily = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Fuente de los bloques y fragmentos de código. Null usa la fuente incluida.</summary>
    private static string? _codeFontFamily;
    public static string? CodeFontFamily
    {
        get => _codeFontFamily;
        set
        {
            if (_codeFontFamily == value) return;
            _codeFontFamily = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Whether the editor's (and preview's) scrollbar is visible.</summary>
    private static bool _showScrollbar = true;
    public static bool ShowScrollbar
    {
        get => _showScrollbar;
        set
        {
            if (_showScrollbar == value) return;
            _showScrollbar = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Number of spaces a Tab press inserts (docs/ajustes-docs/04-editor-y-notificaciones.md §1) —
    /// applied to AvaloniaEdit's <c>TextEditor.Options.IndentationSize</c>, with
    /// <c>ConvertTabsToSpaces = true</c> so it's actually spaces, not a literal tab character.</summary>
    private static int _tabSize = 4;
    public static int TabSize
    {
        get => _tabSize;
        set
        {
            if (_tabSize == value) return;
            _tabSize = value;
            Changed?.Invoke();
        }
    }

    private static bool _lineNumbers;
    public static bool LineNumbers
    {
        get => _lineNumbers;
        set
        {
            if (_lineNumbers == value) return;
            _lineNumbers = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Justifica el texto de las notas (Ajustes › Editor › "Alineación del texto"). Qué bloques
    /// abarca y cómo se reparte el blanco lo decide
    /// <c>Views.Editor.Markdown.MarkdownJustification</c>.</summary>
    private static bool _justifyParagraphs = true;
    public static bool JustifyParagraphs
    {
        get => _justifyParagraphs;
        set
        {
            if (_justifyParagraphs == value) return;
            _justifyParagraphs = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Si escribir un paréntesis, un corchete o unas comillas con texto seleccionado lo envuelve,
    /// y si sin selección se cierra solo (ver <c>EditorView.Editing.cs</c>). Es una preferencia y no una
    /// constante porque el autocierre divide a la gente: a quien escribe código dentro de las notas le
    /// ahorra medio teclado, y a quien escribe prosa con paréntesis sueltos le estorba.</summary>
    private static bool _autoPairs = true;
    public static bool AutoPairs
    {
        get => _autoPairs;
        set
        {
            if (_autoPairs == value) return;
            _autoPairs = value;
            Changed?.Invoke();
        }
    }

    private static bool _wordWrap = true;
    public static bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value) return;
            _wordWrap = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Si el live preview descarga y dibuja las imágenes escritas con una URL <c>http(s)</c>
    /// (Ajustes › Editor). Las imágenes del propio vault y las <c>data:</c> incrustadas en la nota se
    /// dibujan siempre; estas otras no, porque abrir la nota implicaría una petición a un servidor de
    /// terceros — que revela la IP del usuario y el momento exacto en que la lee. Por defecto activo,
    /// como en cualquier editor Markdown, pero desactivable de una sola casilla.</summary>
    private static bool _remoteImages = true;
    public static bool RemoteImages
    {
        get => _remoteImages;
        set
        {
            if (_remoteImages == value) return;
            _remoteImages = value;
            Changed?.Invoke();
        }
    }

    private static bool _sidePanelVisible = true;
    public static bool SidePanelVisible
    {
        get => _sidePanelVisible;
        set
        {
            if (_sidePanelVisible == value) return;
            _sidePanelVisible = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Whether the whole formatting toolbar (all five groups) is shown at all — separate from
    /// the per-group flags below, which only matter while this is true. Hiding it lets the editor's body
    /// row (Grid.Row="1", "Auto"-height in EditorView.axaml) collapse to 0 and hand that space back to
    /// the note body.</summary>
    private static bool _toolbarVisible = true;
    public static bool ToolbarVisible
    {
        get => _toolbarVisible;
        set
        {
            if (_toolbarVisible == value) return;
            _toolbarVisible = value;
            Changed?.Invoke();
        }
    }

    // ===================== Formatting toolbar group visibility =====================
    // One flag per collapsible group in EditorView's formatting toolbar (text style, headings, lists,
    // insert, code) — toggled from EditorViewModel.ToggleToolbarGroup, mirrored here (like
    // SidePanelVisible above) so every other open tab's toolbar reflects the same choice immediately.

    private static bool _toolbarTextStyleExpanded = true;
    public static bool ToolbarTextStyleExpanded
    {
        get => _toolbarTextStyleExpanded;
        set
        {
            if (_toolbarTextStyleExpanded == value) return;
            _toolbarTextStyleExpanded = value;
            Changed?.Invoke();
        }
    }

    private static bool _toolbarHeadingsExpanded = true;
    public static bool ToolbarHeadingsExpanded
    {
        get => _toolbarHeadingsExpanded;
        set
        {
            if (_toolbarHeadingsExpanded == value) return;
            _toolbarHeadingsExpanded = value;
            Changed?.Invoke();
        }
    }

    private static bool _toolbarListsExpanded = true;
    public static bool ToolbarListsExpanded
    {
        get => _toolbarListsExpanded;
        set
        {
            if (_toolbarListsExpanded == value) return;
            _toolbarListsExpanded = value;
            Changed?.Invoke();
        }
    }

    private static bool _toolbarInsertExpanded = true;
    public static bool ToolbarInsertExpanded
    {
        get => _toolbarInsertExpanded;
        set
        {
            if (_toolbarInsertExpanded == value) return;
            _toolbarInsertExpanded = value;
            Changed?.Invoke();
        }
    }

    private static bool _toolbarCodeExpanded = true;
    public static bool ToolbarCodeExpanded
    {
        get => _toolbarCodeExpanded;
        set
        {
            if (_toolbarCodeExpanded == value) return;
            _toolbarCodeExpanded = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Vista simplificada de la página de Notas (Ajustes › Editor › Vista del editor): árbol de
    /// carpetas y notas a la izquierda, pestañas y editor a la derecha, sin barra de formato ni panel
    /// lateral de esquema/enlaces/estadísticas. Vive aquí, y no solo en <c>AppSettings</c>, por el mismo
    /// motivo que <see cref="SidePanelVisible"/>: <c>NotesViewModel</c> y cada <c>EditorViewModel</c>
    /// abierto la reflejan al vuelo cuando se cambia desde Ajustes, sin cerrar y reabrir la nota.</summary>
    private static bool _simplifiedNotesView;
    public static bool SimplifiedNotesView
    {
        get => _simplifiedNotesView;
        set
        {
            if (_simplifiedNotesView == value) return;
            _simplifiedNotesView = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Modo enfoque/zen (propuesta 7, docs/propuesta-nuevas-funcionalidades.md) — oculta la
    /// cabecera, la barra de formato, las tarjetas vinculadas y el panel lateral del editor, además de la
    /// barra de pestañas y el sidebar del shell (ver <c>NotesViewModel.IsZenMode</c>/
    /// <c>MainWindowViewModel.IsZenMode</c>, que se resincronizan con este mismo valor). A diferencia de
    /// <see cref="ToolbarVisible"/>/<see cref="SidePanelVisible"/>, deliberadamente no se persiste en
    /// <c>AppSettings</c> — siempre arranca en <c>false</c> en cada sesión, coherente con ser un modo
    /// transitorio de "ahora mismo quiero concentrarme" y no una preferencia duradera.</summary>
    private static bool _isZenMode;
    public static bool IsZenMode
    {
        get => _isZenMode;
        set
        {
            if (_isZenMode == value) return;
            _isZenMode = value;
            Changed?.Invoke();
        }
    }
}
