using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace enki.Services.Export;

/// <summary>
/// Convenciones de informe para las tablas del PDF de una nota: qué columnas son de cifras (se alinean a la
/// derecha, para que unidades, decenas y decimales queden en columna) y qué fila es la de total (va en
/// negrita sobre gris). Son deliberadamente conservadoras —el contenido de la nota no se toca y una
/// alineación escrita por el usuario siempre manda—: ante la duda, la tabla sale como en el editor.
/// Lógica pura sobre el texto de las celdas, para poder probarla sin maquetar nada.
/// </summary>
internal static class DocumentTableRules
{
    /// <summary>Una cifra con su signo, sus separadores de miles y decimales y, como mucho, un símbolo de
    /// moneda o de porcentaje delante o detrás: <c>1.234,56 €</c>, <c>-12 %</c>, <c>$40</c>, <c>91,6%</c>.
    /// Una fecha (<c>12/03/2025</c>), una hora, un código (<c>A-12</c>) o una cifra con unidad escrita
    /// (<c>12 kg</c>) no lo son.</summary>
    private static readonly Regex Number = new(
        @"^[+\-−]?\s?(?:[€$£]\s?)?\d(?:[\d.,'   ]*\d)?(?:\s?[%€$£])?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TotalLabel = new(
        @"^(?:sub)?total(?:es|s)?\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>El texto de una celda sin los marcadores de énfasis de Markdown que la envuelven
    /// (<c>**Total**</c>, <c>`12`</c>) ni espacios alrededor.</summary>
    public static string PlainText(string cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        return cell.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("~~", string.Empty, StringComparison.Ordinal)
            .Replace("==", string.Empty, StringComparison.Ordinal)
            .Trim()
            .Trim('`', '*', '_')
            .Trim();
    }

    public static bool IsNumeric(string text) => Number.IsMatch(text);

    /// <summary>Si las celdas del cuerpo de una columna (sin la de encabezado, ya pasadas por
    /// <see cref="PlainText"/>) son todas cifras. Las vacías no cuentan, pero tiene que haber al menos una
    /// con contenido.</summary>
    public static bool IsNumericColumn(IEnumerable<string> bodyCells)
    {
        ArgumentNullException.ThrowIfNull(bodyCells);
        var any = false;
        foreach (var cell in bodyCells)
        {
            if (cell.Length == 0) continue;
            if (!IsNumeric(cell)) return false;
            any = true;
        }
        return any;
    }

    /// <summary>Si la primera celda de la última fila la anuncia como fila de total: empieza por «Total»,
    /// «Totales», «Totals» o «Subtotal» como palabra completa («Total general», «TOTAL:»), no por una
    /// palabra que solo empiece igual («Totalmente»).</summary>
    public static bool IsTotalLabel(string text) => TotalLabel.IsMatch(text);
}
