using System;
using System.Collections.Generic;
using Avalonia.Media;
using Material.Icons;

namespace enki.Views.Editor.Markdown;

/// <summary>El tono de un aviso: qué color le corresponde dentro de la paleta de la aplicación. No hay
/// un color por tipo —serían trece— sino cuatro intenciones, que es lo que de verdad distingue un aviso
/// de otro al leerlo de un vistazo.</summary>
public enum MdCalloutTone
{
    /// <summary>Informativo: el gris/tinta neutro del resto de la interfaz.</summary>
    Neutral,
    Success,
    Warning,
    Danger
}

/// <summary>Lo que se sabe de una línea que forma parte de un aviso.</summary>
public readonly record struct MdCalloutLine(string Kind, bool IsHeader, int StartLine, int EndLine)
{
    public MdCalloutTone Tone => MarkdownCallouts.ToneOf(Kind);
}

/// <summary>
/// Avisos («callouts») al estilo de Obsidian: una cita cuya primera línea empieza por <c>[!tipo]</c>,
/// opcionalmente con un título propio detrás.
///
/// <code>
/// &gt; [!warning] Antes de actualizar
/// &gt; Haz una copia del vault.
/// </code>
///
/// Se apoya en la sintaxis de cita que ya existía en vez de inventar una nueva, así que una nota con
/// avisos se sigue viendo bien en cualquier otro editor Markdown: allí se lee como una cita normal cuya
/// primera línea dice <c>[!warning]</c>. Esa compatibilidad es justo el motivo de que Obsidian lo
/// definiera así, y es la razón de respetarla al pie de la letra en vez de mejorarla.
/// </summary>
public static class MarkdownCallouts
{
    /// <summary>Los tipos reconocidos con su tono y su icono. Son los de Obsidian, alias incluidos: una
    /// nota escrita allí tiene que verse igual aquí, y al revés.</summary>
    private static readonly Dictionary<string, (MdCalloutTone Tone, MaterialIconKind Icon)> Kinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["note"] = (MdCalloutTone.Neutral, MaterialIconKind.PencilOutline),
            ["abstract"] = (MdCalloutTone.Neutral, MaterialIconKind.ClipboardTextOutline),
            ["summary"] = (MdCalloutTone.Neutral, MaterialIconKind.ClipboardTextOutline),
            ["tldr"] = (MdCalloutTone.Neutral, MaterialIconKind.ClipboardTextOutline),
            ["info"] = (MdCalloutTone.Neutral, MaterialIconKind.InformationOutline),
            ["todo"] = (MdCalloutTone.Neutral, MaterialIconKind.CheckboxMarkedCircleOutline),
            ["example"] = (MdCalloutTone.Neutral, MaterialIconKind.FormatListBulletedSquare),
            ["quote"] = (MdCalloutTone.Neutral, MaterialIconKind.FormatQuoteClose),
            ["cite"] = (MdCalloutTone.Neutral, MaterialIconKind.FormatQuoteClose),

            ["tip"] = (MdCalloutTone.Success, MaterialIconKind.LightbulbOnOutline),
            ["hint"] = (MdCalloutTone.Success, MaterialIconKind.LightbulbOnOutline),
            ["important"] = (MdCalloutTone.Success, MaterialIconKind.LightbulbOnOutline),
            ["success"] = (MdCalloutTone.Success, MaterialIconKind.CheckCircleOutline),
            ["check"] = (MdCalloutTone.Success, MaterialIconKind.CheckCircleOutline),
            ["done"] = (MdCalloutTone.Success, MaterialIconKind.CheckCircleOutline),

            ["question"] = (MdCalloutTone.Warning, MaterialIconKind.HelpCircleOutline),
            ["help"] = (MdCalloutTone.Warning, MaterialIconKind.HelpCircleOutline),
            ["faq"] = (MdCalloutTone.Warning, MaterialIconKind.HelpCircleOutline),
            ["warning"] = (MdCalloutTone.Warning, MaterialIconKind.AlertOutline),
            ["caution"] = (MdCalloutTone.Warning, MaterialIconKind.AlertOutline),
            ["attention"] = (MdCalloutTone.Warning, MaterialIconKind.AlertOutline),

            ["failure"] = (MdCalloutTone.Danger, MaterialIconKind.CloseCircleOutline),
            ["fail"] = (MdCalloutTone.Danger, MaterialIconKind.CloseCircleOutline),
            ["missing"] = (MdCalloutTone.Danger, MaterialIconKind.CloseCircleOutline),
            ["danger"] = (MdCalloutTone.Danger, MaterialIconKind.FlashOutline),
            ["error"] = (MdCalloutTone.Danger, MaterialIconKind.FlashOutline),
            ["bug"] = (MdCalloutTone.Danger, MaterialIconKind.BugOutline)
        };

    public static bool IsKnown(string kind) => Kinds.ContainsKey(kind);

    public static MdCalloutTone ToneOf(string kind) =>
        Kinds.TryGetValue(kind, out var entry) ? entry.Tone : MdCalloutTone.Neutral;

    public static MaterialIconKind IconOf(string kind) =>
        Kinds.TryGetValue(kind, out var entry) ? entry.Icon : MaterialIconKind.InformationOutline;

    /// <summary>El título que se enseña cuando el aviso no trae uno propio: el tipo con la inicial en
    /// mayúscula, tal cual lo escribió quien redactó la nota. Deliberadamente sin traducir — forma parte
    /// del contenido del documento, no de la interfaz, y traducirlo haría que la misma nota se leyera
    /// distinta según el idioma de la aplicación.</summary>
    public static string DefaultTitle(string kind) =>
        kind.Length == 0 ? kind : char.ToUpperInvariant(kind[0]) + kind[1..].ToLowerInvariant();

    public static IBrush BrushOf(MdCalloutTone tone, MarkdownStyle style) => tone switch
    {
        MdCalloutTone.Success => style.Success,
        MdCalloutTone.Warning => style.Warning,
        MdCalloutTone.Danger => style.Danger,
        _ => style.TextSecondary
    };

    /// <summary>
    /// Recorre el documento una vez y marca qué líneas forman parte de un aviso, cuál es su cabecera y
    /// dónde empieza y acaba cada uno. Es la misma mecánica que
    /// <see cref="MarkdownLineParser.ComputeFences"/> y por el mismo motivo: si una línea de cita es la
    /// continuación de un aviso o una cita normal no se puede contestar mirando solo esa línea.
    ///
    /// Un aviso llega hasta la primera línea que ya no es cita — otra cabecera <c>[!tipo]</c> abre uno
    /// nuevo aunque venga pegada a la anterior, igual que en Obsidian.
    /// </summary>
    public static Dictionary<int, MdCalloutLine> Scan(string documentText, Dictionary<int, FenceInfo> fences)
    {
        var result = new Dictionary<int, MdCalloutLine>();
        if (string.IsNullOrEmpty(documentText)) return result;

        var lines = documentText.Split('\n');
        var openKind = (string?)null;
        var openStart = 0;
        var pending = new List<int>();

        void Close(int endLine)
        {
            if (openKind is null) return;
            foreach (var number in pending)
                result[number] = new MdCalloutLine(openKind, number == openStart, openStart, endLine);
            pending.Clear();
            openKind = null;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var lineNumber = i + 1;
            if (fences.TryGetValue(lineNumber, out var fence) && fence.IsCode)
            {
                Close(lineNumber - 1);
                continue;
            }

            var raw = lines[i].TrimEnd('\r');
            var quote = QuoteContent(raw);
            if (quote is null)
            {
                Close(lineNumber - 1);
                continue;
            }

            if (ParseHeader(quote) is { } kind)
            {
                Close(lineNumber - 1);
                openKind = kind;
                openStart = lineNumber;
            }
            else if (openKind is null)
            {
                continue;
            }
            pending.Add(lineNumber);
        }
        Close(lines.Length);

        return result;
    }

    /// <summary>El texto que va detrás de los <c>&gt;</c> de una línea de cita, o null si no es una cita.
    /// Solo mira el primer nivel: un aviso dentro de otro aviso se comporta como el de dentro, que es lo
    /// que hace Obsidian.</summary>
    private static string? QuoteContent(string raw)
    {
        var i = 0;
        while (i < raw.Length && (raw[i] == ' ' || raw[i] == '\t')) i++;
        if (i > 3 || i >= raw.Length || raw[i] != '>') return null;
        while (i < raw.Length && raw[i] == '>')
        {
            i++;
            if (i < raw.Length && raw[i] == ' ') i++;
        }
        return raw[i..];
    }

    /// <summary>El tipo de aviso que abre <paramref name="content"/> (<c>[!warning]</c>, con el
    /// <c>+</c>/<c>-</c> de plegado opcional detrás), o null si esa línea es una cita normal.</summary>
    public static string? ParseHeader(string content)
    {
        var range = HeaderRange(content);
        return range is { } r ? content[(r.Start.Value + 2)..(r.End.Value - 1)] : null;
    }

    /// <summary>El rango de <paramref name="content"/> que ocupa el marcador <c>[!tipo]</c> más su
    /// <c>+</c>/<c>-</c> y el espacio que lo separa del título — lo que el live preview oculta y sustituye
    /// por el icono. <see cref="System.Range.End"/> apunta justo detrás del <c>]</c>; el resto se devuelve
    /// aparte en <paramref name="afterMarker"/>, donde empieza el título.</summary>
    public static Range? HeaderRange(string content) => HeaderRange(content, out _);

    public static Range? HeaderRange(string content, out int afterMarker)
    {
        afterMarker = 0;
        var i = 0;
        while (i < content.Length && content[i] == ' ') i++;
        if (i + 3 >= content.Length || content[i] != '[' || content[i + 1] != '!') return null;

        var close = content.IndexOf(']', i + 2);
        if (close < 0 || close == i + 2) return null;

        var kind = content[(i + 2)..close];
        foreach (var c in kind)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return null;
        }
        if (!IsKnown(kind)) return null;

        afterMarker = close + 1;
        // El "+"/"-" de plegado de Obsidian: se reconoce y se oculta con el marcador, aunque aquí el
        // aviso no se pliegue todavía — así una nota traída de allí no enseña un signo suelto.
        if (afterMarker < content.Length && (content[afterMarker] == '+' || content[afterMarker] == '-')) afterMarker++;
        if (afterMarker < content.Length && content[afterMarker] == ' ') afterMarker++;
        return new Range(i, close + 1);
    }
}
