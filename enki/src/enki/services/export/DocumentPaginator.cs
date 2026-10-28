using System;
using System.Collections.Generic;

namespace enki.Services.Export;

/// <summary>Lo que el paginador necesita saber de un bloque ya maquetado, sin ver ningún control.</summary>
/// <param name="Height">Alto del bloque.</param>
/// <param name="Breaks">Alturas, de menor a mayor y estrictamente dentro de (0, <paramref name="Height"/>),
/// por las que se puede cortar el bloque sin partir nada por la mitad: el principio de cada línea de texto,
/// de cada fila de tabla, de cada elemento de lista. Entre dos cortes consecutivos hay una «unidad»
/// (una línea, una fila); el bloque tiene <c>Breaks.Count + 1</c> unidades.</param>
/// <param name="FallbackBreaks">Cortes de segunda categoría, para cuando una unidad entera no cabe en una
/// página vacía (una fila de tabla más alta que la hoja): las líneas de dentro de esa unidad.</param>
/// <param name="SpaceBefore">Aire encima del bloque. Se suprime en lo alto de una página, como hace Word:
/// un párrafo que abre página no empieza con un hueco.</param>
/// <param name="KeepWithNext">El bloque no puede quedarse solo al pie de una página (un encabezado): si
/// detrás no cabe al menos el arranque del siguiente, pasa con él a la página siguiente. Tampoco se parte.</param>
/// <param name="RepeatHeight">Alto de la primera unidad que hay que repetir en lo alto de cada página en la
/// que continúe el bloque (la fila de encabezados de una tabla), o 0.</param>
public sealed record PaginationBlock(
    double Height,
    IReadOnlyList<double> Breaks,
    IReadOnlyList<double> FallbackBreaks,
    double SpaceBefore = 0,
    bool KeepWithNext = false,
    double RepeatHeight = 0);

/// <summary>Un trozo de un bloque colocado en una página: la franja [<paramref name="From"/>,
/// <paramref name="To"/>) del bloque, dibujada con su borde superior en <paramref name="Top"/> (medido
/// desde el borde superior de la caja de texto de la página).</summary>
public readonly record struct PageFragment(int Block, double From, double To, double Top)
{
    public double Height => To - From;
}

/// <summary>
/// Reparte una secuencia de bloques en páginas de alto fijo con las reglas de composición de un procesador
/// de textos, que es lo que distingue un PDF «bien maquetado» de una imagen larga cortada a tijera:
/// <list type="bullet">
/// <item>Nunca corta por en medio de una línea, una imagen o una fila de tabla: solo por los
/// <see cref="PaginationBlock.Breaks"/> que le da quien maqueta.</item>
/// <item>Control de viudas y huérfanas: un bloque que se parte deja al menos dos unidades a cada lado (un
/// párrafo de tres líneas no se parte 1 + 2; pasa entero a la página siguiente).</item>
/// <item>Los encabezados no se quedan huérfanos al pie (<see cref="PaginationBlock.KeepWithNext"/>).</item>
/// <item>La fila de encabezados de una tabla se repite en cada página por la que continúe la tabla.</item>
/// <item>El aire de encima de un bloque desaparece cuando el bloque abre página.</item>
/// </list>
/// Las reglas solo se relajan cuando no hay forma de cumplirlas (un bloque que no cabe ni en una página
/// vacía): primero se ignoran viudas y huérfanas, luego se usan los cortes de segunda categoría y, como
/// último recurso, se corta a la altura de la página. El resultado nunca deja la paginación colgada.
/// Es lógica pura, sin Avalonia, para poder probarla sin maquetar nada.
/// </summary>
public static class DocumentPaginator
{
    private const double Epsilon = 0.01;

    /// <summary>Unidades mínimas a cada lado de un corte (control de viudas y huérfanas).</summary>
    private const int MinUnitsPerSide = 2;

    /// <summary>Páginas resultantes, cada una con sus trozos en orden de dibujo. Siempre hay al menos una
    /// página, aunque no haya bloques: un PDF sin hojas no es un documento válido.</summary>
    public static IReadOnlyList<IReadOnlyList<PageFragment>> Paginate(IReadOnlyList<PaginationBlock> blocks,
        double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageHeight);

        var pages = new List<IReadOnlyList<PageFragment>>();
        var current = new List<PageFragment>();
        var y = 0.0;

        void NewPage()
        {
            pages.Add(current);
            current = new List<PageFragment>();
            y = 0;
        }

        for (var index = 0; index < blocks.Count; index++)
        {
            var block = blocks[index];
            var from = 0.0;

            while (from < block.Height - Epsilon)
            {
                var atTop = current.Count == 0;
                var space = atTop || from > 0 ? 0 : block.SpaceBefore;
                var header = from > 0 && block.RepeatHeight > 0 && from >= block.RepeatHeight - Epsilon
                    ? block.RepeatHeight
                    : 0;
                var available = pageHeight - y - space - header;
                var remaining = block.Height - from;

                if (remaining <= available + Epsilon)
                {
                    if (from == 0 && block.KeepWithNext && !atTop
                        && !FitsWithFollowing(blocks, index, y + space + remaining, pageHeight))
                    {
                        NewPage();
                        continue;
                    }

                    Place(block, index, from, block.Height, space, header);
                    from = block.Height;
                    break;
                }

                var cut = block.KeepWithNext ? -1 : ChooseCut(block, from, from + available, strict: true);
                if (cut > from)
                {
                    Place(block, index, from, cut, space, header);
                    from = cut;
                    NewPage();
                    continue;
                }

                if (!atTop)
                {
                    NewPage();
                    continue;
                }

                // En lo alto de una página vacía y aun así no hay corte que respete las reglas: el bloque
                // no cabe en ninguna página tal cual. Se relajan las reglas en orden hasta poder avanzar.
                cut = ChooseCut(block, from, from + available, strict: false);
                if (cut <= from) cut = LargestBreak(block.FallbackBreaks, from, from + available);
                if (cut <= from) cut = from + Math.Max(available, Epsilon * 10);
                cut = Math.Min(cut, block.Height);

                Place(block, index, from, cut, space, header);
                from = cut;
                if (from < block.Height - Epsilon) NewPage();
            }
        }

        if (current.Count > 0 || pages.Count == 0) pages.Add(current);
        return pages;

        void Place(PaginationBlock block, int index, double from, double to, double space, double header)
        {
            y += space;
            if (header > 0)
            {
                current.Add(new PageFragment(index, 0, block.RepeatHeight, y));
                y += header;
            }
            current.Add(new PageFragment(index, from, to, y));
            y += to - from;
        }
    }

    /// <summary>Si detrás de un bloque que acaba en <paramref name="bottom"/> cabe al menos el arranque del
    /// siguiente (sus dos primeras unidades, o el bloque entero si es más corto). Encadena: un encabezado
    /// seguido de otro encabezado necesita también el arranque del tercero. Si ni en una página vacía
    /// cabría la cadena, se responde que sí, para no empujar a la página siguiente algo que tampoco cabrá
    /// allí.</summary>
    private static bool FitsWithFollowing(IReadOnlyList<PaginationBlock> blocks, int index, double bottom,
        double pageHeight)
    {
        var needed = 0.0;
        for (var next = index + 1; next < blocks.Count; next++)
        {
            var following = blocks[next];
            var start = following.Breaks.Count >= MinUnitsPerSide
                ? following.Breaks[MinUnitsPerSide - 1]
                : following.Height;
            needed += following.SpaceBefore + (following.KeepWithNext ? following.Height : start);
            if (!following.KeepWithNext) break;
        }

        if (blocks[index].Height + needed > pageHeight) return true;
        return bottom + needed <= pageHeight + Epsilon;
    }

    /// <summary>El corte más bajo de <see cref="PaginationBlock.Breaks"/> que cae en (<paramref name="from"/>,
    /// <paramref name="limit"/>]. En modo estricto, además, deja al menos <see cref="MinUnitsPerSide"/>
    /// unidades en esta página y otras tantas para la siguiente. Devuelve -1 si no hay ninguno.</summary>
    private static double ChooseCut(PaginationBlock block, double from, double limit, bool strict)
    {
        var breaks = block.Breaks;
        var unitCount = breaks.Count + 1;

        // Unidades que ya quedaron en páginas anteriores: los cortes que hay en o antes de `from`.
        var consumed = 0;
        while (consumed < breaks.Count && breaks[consumed] <= from + Epsilon) consumed++;

        for (var j = breaks.Count - 1; j >= consumed; j--)
        {
            var candidate = breaks[j];
            if (candidate > limit + Epsilon) continue;

            if (strict)
            {
                var placedHere = j + 1 - consumed;
                var left = unitCount - (j + 1);
                if (placedHere < MinUnitsPerSide || left < MinUnitsPerSide) continue;
            }
            return candidate;
        }
        return -1;
    }

    private static double LargestBreak(IReadOnlyList<double> breaks, double from, double limit)
    {
        for (var j = breaks.Count - 1; j >= 0; j--)
        {
            if (breaks[j] > from + Epsilon && breaks[j] <= limit + Epsilon) return breaks[j];
        }
        return -1;
    }
}
