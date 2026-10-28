using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using enki.Views.Editor.Markdown;
using TextMateSharp.Grammars;

namespace enki.Services.Export;

/// <summary>Hoja de papel y márgenes del PDF de una nota, en puntos tipográficos (1/72"). Las dos hojas
/// que se ofrecen son las de Word con sus márgenes «Normal»: A4 con 2,5 cm arriba y abajo y 3 cm a los
/// lados (la plantilla de Word en español), y Carta con 1" por los cuatro lados (la de Word en EE. UU.).
/// Se elige por la cultura de la interfaz con <see cref="ForCulture"/>: quien tiene la app en inglés de
/// EE. UU. o en español de Latinoamérica imprime en Carta; el resto, en A4.</summary>
/// <param name="FooterDistance">Distancia del borde inferior de la hoja a la línea base del pie de página
/// (el número de página): 1,25 cm, la de Word.</param>
public sealed record NotePageSetup(double Width, double Height, double MarginTop, double MarginRight,
    double MarginBottom, double MarginLeft, double FooterDistance)
{
    private const double Centimeter = 72 / 2.54;

    public static NotePageSetup A4 { get; } =
        new(595.28, 841.89, 2.5 * Centimeter, 3 * Centimeter, 2.5 * Centimeter, 3 * Centimeter, 1.25 * Centimeter);

    public static NotePageSetup Letter { get; } = new(612, 792, 72, 72, 72, 72, 1.25 * Centimeter);

    /// <summary>Países donde el papel corriente es Carta y no A4.</summary>
    private static readonly HashSet<string> LetterRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "US", "CA", "MX", "CO", "VE", "CL", "PE", "PH", "GT", "CR", "PA", "DO", "PR", "SV", "HN", "NI"
    };

    public double ContentWidth => Width - MarginLeft - MarginRight;
    public double ContentHeight => Height - MarginTop - MarginBottom;

    public static NotePageSetup ForCulture(CultureInfo culture)
    {
        // "es-419" (español de Latinoamérica) no es un país y RegionInfo no lo acepta, pero la región en la
        // que se usa imprime mayoritariamente en Carta.
        if (culture.Name.EndsWith("-419", StringComparison.Ordinal)) return Letter;

        try
        {
            return LetterRegions.Contains(new RegionInfo(culture.Name).TwoLetterISORegionName) ? Letter : A4;
        }
        catch (ArgumentException)
        {
            return A4;
        }
    }
}

/// <summary>
/// Colores, tipografías y medidas con los que se compone el documento de una nota exportada
/// (<see cref="NoteDocumentComposer"/>). Hay dos variantes:
/// <list type="bullet">
/// <item><see cref="ForPrint"/> — el PDF: tinta negra sobre papel blanco siempre, sea cual sea el tema de
/// la app, porque un PDF se imprime y se comparte; un documento con fondo oscuro gastaría tóner y se leería
/// mal fuera de la pantalla en la que se generó.</item>
/// <item><see cref="ForScreen"/> — el PNG: la paleta del tema activo, la misma que pinta el editor, porque
/// una imagen de la nota se espera tal y como se ve (decisión de T8, ver
/// <c>.docs/project-info/exportar-vistas-imagen-pdf.md</c>).</item>
/// </list>
/// Las tipografías y el tamaño de letra salen siempre de las preferencias del editor
/// (<see cref="EditorPreferences"/>): el documento se adapta a cómo el usuario escribe sus notas, no al
/// revés. Las medidas van en las unidades del documento (puntos en el PDF).
/// </summary>
public sealed class NoteDocumentStyle
{
    public required IBrush Paper { get; init; }
    public required IBrush Ink { get; init; }
    public required IBrush InkSecondary { get; init; }
    public required IBrush InkMuted { get; init; }
    public required IBrush Rule { get; init; }
    public required IBrush CodeBackground { get; init; }
    public required IBrush CodeBorder { get; init; }
    public required IBrush InlineCodeBackground { get; init; }
    public required IBrush TableBorder { get; init; }
    public required IBrush TableHeaderBackground { get; init; }
    public required IBrush Highlight { get; init; }
    public required IBrush Success { get; init; }
    public required IBrush Warning { get; init; }
    public required IBrush Danger { get; init; }

    public required FontFamily BodyFont { get; init; }
    public required FontFamily MonoFont { get; init; }

    /// <summary>Cuerpo de letra del texto normal.</summary>
    public required double FontSize { get; init; }

    /// <summary>Si los párrafos van justificados, como en el editor (Ajustes › Editor).</summary>
    public required bool Justify { get; init; }

    /// <summary>Tema TextMate de los bloques de código: claro sobre papel, el del editor en pantalla.</summary>
    public required ThemeName CodeTheme { get; init; }

    /// <summary>Interlineado del texto corrido: algo más abierto que el 1,15 de Word, porque una nota es
    /// texto de lectura con listas y código intercalados, no un formulario.</summary>
    public double LineHeight => Math.Round(FontSize * 1.45, 2);

    public double CodeFontSize => Math.Round(FontSize * 0.88, 2);

    public double CodeLineHeight => Math.Round(CodeFontSize * 1.45, 2);

    /// <summary>Puntos de documento por píxel del editor. El editor mide en píxeles lógicos (96 ppp) y el
    /// PDF en puntos (72 ppp): una imagen escrita como <c>![…|300](…)</c> ocupa en el papel lo mismo que
    /// ocuparía impresa desde un navegador.</summary>
    public double PointsPerPixel { get; init; } = 0.75;

    public double HeadingSize(int level) => Math.Round(FontSize * MarkdownStyle.HeadingScale(level), 2);

    /// <summary>Estilo del PDF: papel blanco y tinta negra, con los grises del sistema de diseño
    /// (blanco/negro/grises, sin color de marca — ver <c>sistema-diseno-web.md</c>) y los tonos de los
    /// avisos algo más oscuros que en pantalla para que se lean impresos.</summary>
    public static NoteDocumentStyle ForPrint() => new()
    {
        Paper = Brush("#FFFFFF"),
        Ink = Brush("#141414"),
        InkSecondary = Brush("#3A3A3A"),
        InkMuted = Brush("#6B6B6B"),
        Rule = Brush("#C9C9C9"),
        CodeBackground = Brush("#F5F5F5"),
        CodeBorder = Brush("#E0E0E0"),
        InlineCodeBackground = Brush("#EFEFEF"),
        TableBorder = Brush("#CFCFCF"),
        TableHeaderBackground = Brush("#F0F0F0"),
        Highlight = new SolidColorBrush(Color.Parse("#F5C542"), 0.38),
        Success = Brush("#2E8B57"),
        Warning = Brush("#B7791F"),
        Danger = Brush("#C53030"),
        BodyFont = EditorBodyFont(),
        MonoFont = EditorMonoFont(),
        FontSize = PrintFontSize(EditorPreferences.FontSize),
        Justify = EditorPreferences.JustifyParagraphs,
        CodeTheme = ThemeName.LightPlus
    };

    /// <summary>Estilo del PNG: la paleta del editor tal y como la resuelve <see cref="MarkdownStyle"/>
    /// en el tema activo, sobre el fondo de la lámina.</summary>
    public static NoteDocumentStyle ForScreen(ExportAppearance appearance)
    {
        var editor = MarkdownStyle.FromApp(EditorBodyFont(), EditorMonoFont(), EditorPreferences.FontSize);
        var light = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light;
        return new NoteDocumentStyle
        {
            Paper = new SolidColorBrush(appearance.Background),
            Ink = editor.TextPrimary,
            InkSecondary = editor.TextSecondary,
            InkMuted = editor.TextMuted,
            Rule = editor.CodeBlockBorder,
            CodeBackground = editor.CodeBlockBackground,
            CodeBorder = editor.CodeBlockBorder,
            InlineCodeBackground = editor.CodeInlineBackground,
            TableBorder = editor.TableBorder,
            TableHeaderBackground = editor.TableHeaderBackground,
            Highlight = editor.HighlightBackground,
            Success = editor.Success,
            Warning = editor.Warning,
            Danger = editor.Danger,
            BodyFont = editor.BodyFontFamily,
            MonoFont = editor.MonoFontFamily,
            FontSize = PrintFontSize(EditorPreferences.FontSize),
            Justify = EditorPreferences.JustifyParagraphs,
            CodeTheme = light ? ThemeName.LightPlus : ThemeName.DarkPlus
        };
    }

    /// <summary>El tamaño de letra del editor (en píxeles) pasado a puntos, acotado a lo que sigue siendo
    /// un cuerpo de documento: con el valor por defecto del editor (14,5 px) sale ~11 pt, el cuerpo de
    /// Word.</summary>
    internal static double PrintFontSize(double editorPixels) =>
        Math.Round(Math.Clamp(editorPixels * 0.75, 9, 14), 2);

    /// <summary>Misma resolución que <c>EditorView.ResolveEditorFontFamily</c>: la fuente elegida en
    /// Ajustes › Editor o, si no hay ninguna, la empaquetada.</summary>
    private static FontFamily EditorBodyFont() => string.IsNullOrWhiteSpace(EditorPreferences.FontFamily)
        ? new FontFamily("avares://enki/assets/fonts#Space Grotesk")
        : new FontFamily(EditorPreferences.FontFamily);

    private static FontFamily EditorMonoFont() => string.IsNullOrWhiteSpace(EditorPreferences.CodeFontFamily)
        ? new FontFamily("avares://enki/assets/fonts#JetBrainsMono Nerd Font Mono")
        : new FontFamily(EditorPreferences.CodeFontFamily);

    private static SolidColorBrush Brush(string hex) => new(Color.Parse(hex));
}
