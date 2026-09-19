using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using TmFontStyle = TextMateSharp.Themes.FontStyle;
using TmTheme = TextMateSharp.Themes.Theme;
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
    private const int MaxBlockLines = 2000;

    private static readonly TimeSpan TokenizeBudget = TimeSpan.FromMilliseconds(200);

    private readonly LivePreviewState _state;
    private readonly Dictionary<int, Block> _blocks = new();
    private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IBrush> _brushes = new();

    private RegistryOptions? _options;
    private Registry? _registry;
    private TmTheme? _theme;
    private ThemeName _loadedTheme;
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
        if (_registry is null || _options is null || CurrentTheme() == _loadedTheme) return;
        _loadedTheme = CurrentTheme();
        _registry.SetTheme(_options.LoadTheme(_loadedTheme));
        _theme = _registry.GetTheme();
        _brushes.Clear();
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
        var grammar = GrammarFor(language);
        if (grammar is null || _theme is null) return block;

        IStateStack? stack = null;
        var lineNumber = openingLine + 1;
        var clock = Stopwatch.StartNew();
        for (var count = 0; count < MaxBlockLines && lineNumber <= document.LineCount; count++, lineNumber++)
        {
            var budget = TokenizeBudget - clock.Elapsed;
            if (budget <= TimeSpan.Zero || !_state.GetFence(lineNumber).IsInside) break;
            var text = document.GetText(document.GetLineByNumber(lineNumber));

            ITokenizeLineResult result;
            try
            {
                result = grammar.TokenizeLine(new LineText(text), stack!, budget);
            }
            catch (Exception ex)
            {
                // Una gramática que se atraganta con una línea concreta no puede tumbar el repintado de
                // la nota: se deja el resto del bloque sin colorear y se sigue.
                LogService.Warning($"No se pudo resaltar el bloque de código '{language}'", ex);
                break;
            }

            stack = result.RuleStack;
            block.Lines[lineNumber] = Convert(result.Tokens, text.Length);
        }
        return block;
    }

    private List<CodeToken> Convert(IToken[] tokens, int lineLength)
    {
        var converted = new List<CodeToken>(tokens.Length);
        foreach (var token in tokens)
        {
            var start = Math.Clamp(token.StartIndex, 0, lineLength);
            var end = Math.Clamp(token.EndIndex, 0, lineLength);
            if (end <= start) continue;

            var foreground = -1;
            var style = TmFontStyle.NotSet;
            foreach (var rule in _theme!.Match(token.Scopes))
            {
                if (foreground == -1 && rule.foreground > 0) foreground = rule.foreground;
                if (style == TmFontStyle.NotSet && rule.fontStyle != TmFontStyle.NotSet)
                    style = rule.fontStyle;
                if (foreground != -1 && style != TmFontStyle.NotSet) break;
            }

            if (style == TmFontStyle.NotSet) style = TmFontStyle.None;
            converted.Add(new CodeToken(start, end, BrushFor(foreground),
                style.HasFlag(TmFontStyle.Italic) ? FontStyle.Italic : FontStyle.Normal,
                style.HasFlag(TmFontStyle.Bold) ? FontWeight.Bold : FontWeight.Normal,
                style.HasFlag(TmFontStyle.Underline)));
        }
        return converted;
    }

    private IBrush? BrushFor(int colorId)
    {
        if (colorId <= 0 || _theme is null) return null;
        if (_brushes.TryGetValue(colorId, out var cached)) return cached;
        if (_theme.GetColor(colorId) is not { Length: > 0 } hex || !Color.TryParse(hex, out var color)) return null;
        var brush = new ImmutableSolidColorBrush(color);
        _brushes[colorId] = brush;
        return brush;
    }

    private IGrammar? GrammarFor(string language)
    {
        if (_grammars.TryGetValue(language, out var cached)) return cached;

        IGrammar? grammar = null;
        try
        {
            if (ScopeFor(language) is { } scope) grammar = _registry!.LoadGrammar(scope);
        }
        catch (Exception ex)
        {
            // Lenguaje sin gramática o gramática rota: el bloque se ve sin colores, como hasta ahora.
            LogService.Warning($"No se pudo cargar la gramática de '{language}'", ex);
        }
        _grammars[language] = grammar;
        return grammar;
    }

    /// <summary>El ámbito TextMate que corresponde al lenguaje escrito tras los acentos graves. Se prueba
    /// por identificador, por alias y por extensión, que es como la gente escribe de verdad el lenguaje de
    /// un bloque: <c>csharp</c>, <c>c#</c> y <c>cs</c> tienen que llevar todos al mismo sitio.</summary>
    private string? ScopeFor(string language)
    {
        var options = _options!;
        var trimmed = language.Trim();
        if (trimmed.Length == 0) return null;

        foreach (var candidate in options.GetAvailableLanguages())
        {
            if (string.Equals(candidate.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                return options.GetScopeByLanguageId(candidate.Id);
            if (candidate.Aliases is null) continue;
            foreach (var alias in candidate.Aliases)
            {
                if (string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
                    return options.GetScopeByLanguageId(candidate.Id);
            }
        }
        var byExtension = options.GetLanguageByExtension("." + trimmed);
        return byExtension is null ? null : options.GetScopeByLanguageId(byExtension.Id);
    }

    /// <summary>Si las gramáticas ya están en memoria. Si no lo están, arranca la carga en segundo plano y
    /// devuelve false: la nota se dibuja igual, y cuando termine se pide un repintado.</summary>
    private bool EnsureLoaded()
    {
        if (_registry is not null) return true;
        if (_loading) return false;

        _loading = true;
        var themeName = CurrentTheme();
        Task.Run(() =>
        {
            try
            {
                var options = new RegistryOptions(themeName);
                var registry = new Registry(options);
                var theme = registry.GetTheme();
                Dispatcher.UIThread.Post(() =>
                {
                    _options = options;
                    _registry = registry;
                    _theme = theme;
                    _loadedTheme = themeName;
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
