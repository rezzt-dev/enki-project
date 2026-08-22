using System.Collections.Generic;
using System.Linq;

namespace enki.Services;

/// <summary>Whether a dashboard section's own content needs generous width to read well. <c>Wide</c>
/// sections (accesos rápidos, tareas del tablero, notas recientes) each have their own internal
/// multi-column layout (a 4-up grid, a 3-column board, a featured-card + list split) that would look
/// cramped squeezed into a narrow slot; <c>Compact</c> sections (hoy, almacén, actividad, sesiones) are
/// a single stat block or short list and read fine narrow.</summary>
public enum DashboardSectionSize
{
    Wide,
    Compact
}

/// <summary>One section's placement on the Dashboard's 6-column grid.</summary>
public readonly record struct DashboardGridSlot(int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>Computes where each visible Dashboard section lands on screen for a chosen layout template
/// (Ajustes › Dashboard › Organización). <see cref="Models.VaultSettings.DashboardSectionOrder"/> and
/// <see cref="Models.VaultSettings.DashboardHiddenSections"/> still decide which sections show and in
/// what priority order — this only decides the geometry they're poured into, so switching templates
/// never requires reconfiguring the section list.
///
/// Every non-<see cref="Classic"/> template defines exactly 3 <see cref="DashboardSectionSize.Wide"/>
/// slots and 4 <see cref="DashboardSectionSize.Compact"/> slots — matching the exact count of wide/compact
/// section keys that exist — so every section gets a slot when all 7 are visible. Hiding sections just
/// leaves the matching slots empty (a template never re-packs around a gap): acceptable, since the common
/// case is most/all sections visible, and an underfilled row reads as intentional whitespace rather than
/// a broken layout.</summary>
public static class DashboardLayoutEngine
{
    public const string Classic = "Classic";
    public const string InvertedPyramid = "InvertedPyramid";
    public const string Lateral = "Lateral";
    public const string ModularGrid = "ModularGrid";
    public const string Mosaic = "Mosaic";

    /// <summary>Every known style key, in the order offered to the user in Ajustes › Dashboard.</summary>
    public static readonly string[] AllStyles = [Classic, InvertedPyramid, Lateral, ModularGrid, Mosaic];

    public const int ColumnCount = 6;

    /// <summary>Whether <paramref name="key"/> is a <see cref="DashboardSectionSize.Wide"/> section —
    /// exposed so callers building a layout's thumbnail diagram (Ajustes › Dashboard › Organización) can
    /// shade wide/compact slots differently without duplicating this table.</summary>
    public static bool IsWideSection(string key) => SectionSizes.GetValueOrDefault(key) == DashboardSectionSize.Wide;

    private static readonly Dictionary<string, DashboardSectionSize> SectionSizes = new()
    {
        ["QuickAccess"] = DashboardSectionSize.Wide,
        ["BoardTasks"] = DashboardSectionSize.Wide,
        ["RecentNotes"] = DashboardSectionSize.Wide,
        ["Today"] = DashboardSectionSize.Compact,
        ["Vault"] = DashboardSectionSize.Compact,
        ["Activity"] = DashboardSectionSize.Compact,
        ["Sessions"] = DashboardSectionSize.Compact,

        // Propuesta 13 (docs/propuesta-nuevas-funcionalidades.md): añadida después de las 7 secciones
        // originales que las plantillas de abajo ya reparten exactas (3 Wide + 4 Compact) — no tiene hueco
        // propio en ninguna, así que cae en el "leftover" que Compute() ya documenta: una fila a todo lo
        // ancho debajo de la plantilla en vez de desaparecer o forzar un rediseño de las 4 plantillas.
        ["Habits"] = DashboardSectionSize.Compact
    };

    private readonly record struct TemplateSlot(int Row, int Column, int RowSpan, int ColumnSpan, DashboardSectionSize Size);

    private static readonly Dictionary<string, TemplateSlot[]> Templates = new()
    {
        // Pirámide invertida: un titular a todo lo ancho, un par a media anchura, un trío a un tercio,
        // y una fila de cierre a todo lo ancho — el mismo "grande → mediano → pequeño → cierre" de la
        // pirámide invertida periodística.
        [InvertedPyramid] =
        [
            new TemplateSlot(0, 0, 1, 6, DashboardSectionSize.Wide),
            new TemplateSlot(1, 0, 1, 3, DashboardSectionSize.Wide),
            new TemplateSlot(1, 3, 1, 3, DashboardSectionSize.Wide),
            new TemplateSlot(2, 0, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(2, 2, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(2, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(3, 0, 1, 6, DashboardSectionSize.Compact)
        ],

        // Patrón lateral: un titular a todo lo ancho, luego una columna principal (dos secciones anchas
        // apiladas) junto a una barra lateral angosta de dos secciones compactas apiladas a la derecha —
        // como el cuerpo de un artículo con una barra lateral de widgets — y una fila de cierre a todo lo
        // ancho con el par de compactas restante. Antes la barra lateral apilaba las 4 compactas mientras
        // la columna principal solo llegaba a 2 filas: la rejilla (todas sus filas son Auto) terminaba
        // esas 2 filas de más a la altura corta de una tarjeta compacta sola, dejando un hueco muerto de
        // 4 columnas de ancho bajo la columna principal. Repartir las compactas 2+2 (barra lateral +
        // cierre) hace que cada fila tenga contenido en las 6 columnas, sin huecos.
        [Lateral] =
        [
            new TemplateSlot(0, 0, 1, 6, DashboardSectionSize.Wide),
            new TemplateSlot(1, 0, 1, 4, DashboardSectionSize.Wide),
            new TemplateSlot(1, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(2, 0, 1, 4, DashboardSectionSize.Wide),
            new TemplateSlot(2, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(3, 0, 1, 3, DashboardSectionSize.Compact),
            new TemplateSlot(3, 3, 1, 3, DashboardSectionSize.Compact)
        ],

        // Rejilla modular: fichas de tamaño mixto tipo "bento" — ancha+compacta, tres compactas, dos
        // medianas — sin la progresión monótona de la pirámide ni la columna lateral persistente.
        [ModularGrid] =
        [
            new TemplateSlot(0, 0, 1, 4, DashboardSectionSize.Wide),
            new TemplateSlot(0, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(1, 0, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(1, 2, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(1, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(2, 0, 1, 3, DashboardSectionSize.Wide),
            new TemplateSlot(2, 3, 1, 3, DashboardSectionSize.Wide)
        ],

        // Mosaico: una ficha "destacada" alta junto a un par compacto vertical, luego un par compacto y
        // un par ancho — un ritmo asimétrico distinto a los otros tres.
        [Mosaic] =
        [
            new TemplateSlot(0, 0, 2, 4, DashboardSectionSize.Wide),
            new TemplateSlot(0, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(1, 4, 1, 2, DashboardSectionSize.Compact),
            new TemplateSlot(2, 0, 1, 3, DashboardSectionSize.Compact),
            new TemplateSlot(2, 3, 1, 3, DashboardSectionSize.Compact),
            new TemplateSlot(3, 0, 1, 3, DashboardSectionSize.Wide),
            new TemplateSlot(3, 3, 1, 3, DashboardSectionSize.Wide)
        ]
    };

    /// <summary>Computes each visible section's grid placement. <paramref name="orderedKeys"/> must
    /// already be filtered to visible sections, in the priority order Ajustes › Dashboard's section list
    /// gives them — that order decides which wide section gets the most prominent wide slot, and
    /// likewise among compact sections, independently of each other.</summary>
    public static Dictionary<string, DashboardGridSlot> Compute(IReadOnlyList<string> orderedKeys, string style)
    {
        var result = new Dictionary<string, DashboardGridSlot>();

        if (style != Classic && Templates.TryGetValue(style, out var slots))
        {
            var wideQueue = new Queue<string>(orderedKeys.Where(k => SectionSizes.GetValueOrDefault(k) == DashboardSectionSize.Wide));
            var compactQueue = new Queue<string>(orderedKeys.Where(k => SectionSizes.GetValueOrDefault(k) == DashboardSectionSize.Compact));

            foreach (var slot in slots)
            {
                var queue = slot.Size == DashboardSectionSize.Wide ? wideQueue : compactQueue;
                if (queue.Count == 0) continue;
                result[queue.Dequeue()] = new DashboardGridSlot(slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan);
            }

            // Belt and braces: a template is designed to have exactly one slot per known section key, so
            // this should never fire — but if a future section is added without a matching template slot,
            // it still gets a full-width row below the template instead of silently vanishing.
            if (wideQueue.Count > 0 || compactQueue.Count > 0)
            {
                var nextRow = slots.Max(s => s.Row + s.RowSpan);
                foreach (var leftover in wideQueue.Concat(compactQueue))
                    result[leftover] = new DashboardGridSlot(nextRow++, 0, 1, ColumnCount);
            }

            return result;
        }

        // Classic (and the fallback for any unrecognized style key): one full-width row per section, in
        // order — the Dashboard's original behaviour before Organización existed.
        for (var i = 0; i < orderedKeys.Count; i++)
            result[orderedKeys[i]] = new DashboardGridSlot(i, 0, 1, ColumnCount);

        return result;
    }
}
