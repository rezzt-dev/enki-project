using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using TextMateSharp.Grammars;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>Un tramo coloreado de una línea de código: desde dónde hasta dónde, con qué color y con qué
/// estilo de letra.</summary>
public readonly record struct CodeToken(int Start, int End, IBrush? Foreground, FontStyle Style,
    FontWeight Weight, bool Underline);

/// <summary>
/// Resaltado de sintaxis dentro de los bloques de código de una nota (<c>```python</c>…), con las mismas
/// gramáticas TextMate y el mismo tema que ya usa el visor de código de la sección Archivos — para que un
/// fragmento pegado en una nota y el archivo del que salió se lean igual.
///
/// Hay tres cosas que este resaltado tiene que resolver y que un colorizador de línea suelta no puede:
/// <list type="bullet">
/// <item><b>El contexto.</b> Una gramática TextMate tokeniza una línea <em>a partir del estado en que
/// quedó la anterior</em> (dentro de una cadena, dentro de un comentario de varias líneas…), así que el
/// bloque se tokeniza entero de una vez, desde su delimitador de apertura, y se guardan los tramos de cada
/// línea. AvaloniaEdit solo pide las líneas visibles, y en un bloque largo empezar por la de arriba del
/// todo es la única forma de que la de en medio salga bien.</item>
/// <item><b>El coste.</b> Cargar las gramáticas lleva su tiempo, así que se hace una sola vez y en
/// segundo plano: hasta que están listas el código se ve como se veía antes (monoespaciado y apagado) y
/// entonces se pide un repintado. Escribir en la nota nunca espera a esto.</item>
/// <item><b>La caché.</b> Se repinta en cada pulsación de tecla y en cada movimiento del cursor; volver a
/// tokenizar ahí sería inasumible. El resultado se guarda por bloque y solo se tira cuando el documento
/// vuelve a analizarse (ver <c>EditorView.RecomputeFences</c>).</item>
/// </list>
/// </summary>
public sealed class MarkdownCodeHighlighter
{
    /// <summary>Tope de líneas que se tokenizan por bloque. Un bloque más largo que esto dentro de una
    /// nota es un volcado, no un fragmento para leer; se deja sin colorear antes que meter una pausa en
    /// el repintado.</summary>
    internal const int MaxBlockLines = 2000;

    internal static readonly TimeSpan TokenizeBudget = TimeSpan.FromMilliseconds(200);

    private readonly LivePreviewState _state;
    private readonly Dictionary<int, Block> _blocks = new();

    private CodeTokenizer? _tokenizer;
    private bool _loading;
    private int _version;

    /// <summary>Se dispara cuando las gramáticas acaban de cargarse (o cuando el tema cambia y hay que
    /// recolorear). Quien lo escucha se limita a repintar.</summary>
    public event Action? Changed;

    public MarkdownCodeHighlighter(LivePreviewState state) => _state = state;

    /// <summary>Tira lo tokenizado: el documento ha cambiado. Es barato — lo caro se vuelve a hacer solo
    /// para los bloques que de verdad se dibujen.</summary>
    public void Invalidate()
    {
        _blocks.Clear();
        _version++;
    }

    /// <summary>Recarga el tema si el de la aplicación ya no es el que estaba cargado (claro ↔ oscuro).
    /// La llama la vista cuando cambia la apariencia.</summary>
    public void RefreshTheme()
    {
        if (_tokenizer is null || CurrentTheme() == _tokenizer.Theme) return;
        _tokenizer.SetTheme(CurrentTheme());
        Invalidate();
        Changed?.Invoke();
    }

    /// <summary>Los tramos coloreados de una línea de dentro de un bloque de código, o null si no hay nada
    /// que colorear ahí: la línea no es código, el bloque no declara lenguaje, no hay gramática para ese
    /// lenguaje, o las gramáticas todavía se están cargando.</summary>
    public IReadOnlyList<CodeToken>? Tokens(TextDocument document, int lineNumber)
    {
        var fence = _state.GetFence(lineNumber);
        // Los delimitadores (```lang) se quedan con su color apagado de marcador, que es lo que son.
        if (!fence.IsInside || string.IsNullOrEmpty(fence.Language)) return null;
        if (!EnsureLoaded()) return null;

        var start = OpeningLine(lineNumber);
        if (!_blocks.TryGetValue(start, out var block) || block.Version != _version)
        {
            block = Tokenize(document, start, fence.Language);
            _blocks[start] = block;
        }
        return block.Lines.TryGetValue(lineNumber, out var tokens) ? tokens : null;
    }

    /// <summary>El número de línea del <c>```</c> que abre el bloque al que pertenece
    /// <paramref name="lineNumber"/> — la clave con la que se guarda el bloque tokenizado.</summary>
    private int OpeningLine(int lineNumber)
    {
        var line = lineNumber;
        while (line > 1 && _state.GetFence(line - 1).IsInside) line--;
        return line - 1;
    }

    private Block Tokenize(TextDocument document, int openingLine, string language)
    {
        var block = new Block(_version);
        if (_tokenizer is null) return block;

        var lines = new List<string>();
        for (var lineNumber = openingLine + 1;
             lines.Count < MaxBlockLines && lineNumber <= document.LineCount && _state.GetFence(lineNumber).IsInside;
             lineNumber++)
        {
            lines.Add(document.GetText(document.GetLineByNumber(lineNumber)));
        }

        var tokenized = _tokenizer.TokenizeLines(lines, language, TokenizeBudget);
        if (tokenized is null) return block;
        for (var i = 0; i < tokenized.Count; i++) block.Lines[openingLine + 1 + i] = tokenized[i];
        return block;
    }

    /// <summary>Si las gramáticas ya están en memoria. Si no lo están, arranca la carga en segundo plano y
    /// devuelve false: la nota se dibuja igual, y cuando termine se pide un repintado.</summary>
    private bool EnsureLoaded()
    {
        if (_tokenizer is not null) return true;
        if (_loading) return false;

        _loading = true;
        var themeName = CurrentTheme();
        Task.Run(() =>
        {
            try
            {
                var tokenizer = new CodeTokenizer(themeName);
                Dispatcher.UIThread.Post(() =>
                {
                    _tokenizer = tokenizer;
                    _loading = false;
                    RefreshTheme();
                    Changed?.Invoke();
                });
            }
            catch (Exception ex)
            {
                LogService.Warning("No se pudieron cargar las gramáticas de resaltado de código", ex);
                Dispatcher.UIThread.Post(() => _loading = false);
            }
        });
        return false;
    }

    /// <summary>El mismo par de temas que usa el visor de código de Archivos, por el mismo motivo: un
    /// bloque oscuro en mitad de una aplicación clara (o al revés) se lee como un elemento traído de
    /// fuera.</summary>
    private static ThemeName CurrentTheme() =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus;

    private sealed class Block(int version)
    {
        public int Version { get; } = version;
        public Dictionary<int, List<CodeToken>> Lines { get; } = new();
    }
}
