using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using enki.Services;
using TmFontStyle = TextMateSharp.Themes.FontStyle;
using TmTheme = TextMateSharp.Themes.Theme;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// Gramáticas y tema TextMate ya cargados, más la traducción de sus tokens a <see cref="CodeToken"/>. Es la
/// parte de <see cref="MarkdownCodeHighlighter"/> que no depende del editor: la comparten el resaltado en
/// vivo de los bloques de código y la exportación de notas a PDF, para que un fragmento de código se lea
/// igual en pantalla que en el documento exportado.
///
/// <para>Construirlo carga el registro de gramáticas, que lleva su tiempo: quien lo use desde la interfaz
/// debe crearlo en segundo plano (como hace el resaltador). Una instancia no es segura entre hilos; cada
/// consumidor tiene la suya.</para>
/// </summary>
public sealed class CodeTokenizer
{
    private readonly RegistryOptions _options;
    private readonly Registry _registry;
    private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, IBrush> _brushes = new();
    private TmTheme _theme;

    public CodeTokenizer(ThemeName theme)
    {
        _options = new RegistryOptions(theme);
        _registry = new Registry(_options);
        _theme = _registry.GetTheme();
        Theme = theme;
    }

    public ThemeName Theme { get; private set; }

    /// <summary>Cambia de tema conservando las gramáticas ya cargadas: lo único que depende del tema son
    /// los colores, que se vuelven a resolver la próxima vez que se pidan.</summary>
    public void SetTheme(ThemeName theme)
    {
        if (theme == Theme) return;
        Theme = theme;
        _registry.SetTheme(_options.LoadTheme(theme));
        _theme = _registry.GetTheme();
        _brushes.Clear();
    }

    /// <summary>Tokeniza las líneas de un bloque de código de una sola pasada, arrastrando el estado de la
    /// gramática de una línea a la siguiente (una cadena o un comentario que siguen abiertos). Devuelve los
    /// tramos de cada línea en orden; si el lenguaje no tiene gramática, devuelve null, y si la gramática
    /// se atraganta o se agota <paramref name="budget"/>, la lista se corta ahí y el resto del bloque queda
    /// sin colorear.</summary>
    public IReadOnlyList<List<CodeToken>>? TokenizeLines(IReadOnlyList<string> lines, string language, TimeSpan budget)
    {
        var grammar = GrammarFor(language);
        if (grammar is null) return null;

        var result = new List<List<CodeToken>>(lines.Count);
        IStateStack? stack = null;
        var clock = Stopwatch.StartNew();
        foreach (var text in lines)
        {
            var remaining = budget - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) break;

            ITokenizeLineResult tokenized;
            try
            {
                tokenized = grammar.TokenizeLine(new LineText(text), stack!, remaining);
            }
            catch (Exception ex)
            {
                // Una gramática que se atraganta con una línea concreta no puede tumbar a quien pinta el
                // bloque: se deja el resto sin colorear y se sigue.
                LogService.Warning($"No se pudo resaltar el bloque de código '{language}'", ex);
                break;
            }

            stack = tokenized.RuleStack;
            result.Add(Convert(tokenized.Tokens, text.Length));
        }
        return result;
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
            foreach (var rule in _theme.Match(token.Scopes))
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
        if (colorId <= 0) return null;
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
            if (ScopeFor(language) is { } scope) grammar = _registry.LoadGrammar(scope);
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
        var trimmed = language.Trim();
        if (trimmed.Length == 0) return null;

        foreach (var candidate in _options.GetAvailableLanguages())
        {
            if (string.Equals(candidate.Id, trimmed, StringComparison.OrdinalIgnoreCase))
                return _options.GetScopeByLanguageId(candidate.Id);
            if (candidate.Aliases is null) continue;
            foreach (var alias in candidate.Aliases)
            {
                if (string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase))
                    return _options.GetScopeByLanguageId(candidate.Id);
            }
        }
        var byExtension = _options.GetLanguageByExtension("." + trimmed);
        return byExtension is null ? null : _options.GetScopeByLanguageId(byExtension.Id);
    }
}
