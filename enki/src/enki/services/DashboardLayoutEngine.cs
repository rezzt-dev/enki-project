using System;
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
    public const string Custom = "Custom";

    /// <summary>Every known style key, in the order offered to the user in Ajustes › Dashboard.</summary>
    public static readonly string[] AllStyles = [Classic, InvertedPyramid, Lateral, ModularGrid, Mosaic, Custom];

    public const int DefaultRows = 4;
    public const int DefaultColumns = 6;

    /// <summary>Largest either grid dimension can be. El tope era 6 mientras el Dashboard vertía las
    /// secciones en un <c>Grid</c> de columnas estrella escrito a mano en XAML (Avalonia puede quedarse
    /// midiendo en bucle una rejilla estrella grande dentro de un ScrollViewer). Desde el editor de
    /// rejilla lo pinta <see cref="Controls.DashboardGridPanel"/>, que mide en una sola pasada y reparte
    /// anchos él mismo, así que la restricción real pasa a ser de legibilidad: por debajo de ~1/12 del
    /// ancho una tarjeta ya no muestra nada útil.</summary>
    public const int MaxGridDimension = 12;

    /// <summary>Tamaño por defecto (en columnas) de una ficha recién colocada en la rejilla
    /// personalizada: media rejilla para las secciones <see cref="DashboardSectionSize.Wide"/> (tienen su
    /// propio layout multi-columna dentro) y un tercio para las <see cref="DashboardSectionSize.Compact"/>,
    /// siempre al menos 1 columna. Se usa tanto al auto-colocar una sección sin posición guardada como al
    /// devolver una sección oculta a la rejilla desde la bandeja de Ajustes.</summary>
    public static int DefaultSpanFor(string key, int columns)
    {
        var cols = Math.Max(1, Math.Min(columns, MaxGridDimension));
        var span = IsWideSection(key) ? (int)Math.Ceiling(cols / 2.0) : (int)Math.Ceiling(cols / 3.0);
        return Math.Clamp(span, 1, cols);
    }

    /// <summary>Backwards-compatible alias for <see cref="DefaultColumns"/> — the Classic layout and
    /// the template engine's leftover row still pour sections into a 6-column grid.</summary>
    public const int ColumnCount = DefaultColumns;

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
    /// <param name="customLayout">Optional per-section [Row, Column, RowSpan, ColumnSpan] positions —
    /// used only when <paramref name="style"/> is <see cref="Custom"/>.</param>
    /// <param name="gridRows">Number of rows when <paramref name="style"/> is <see cref="Custom"/>.</param>
    /// <param name="gridColumns">Number of columns when <paramref name="style"/> is <see cref="Custom"/>.</param>
    public static Dictionary<string, DashboardGridSlot> Compute(
        IReadOnlyList<string> orderedKeys,
        string style,
        IReadOnlyDictionary<string, int[]>? customLayout = null,
        int gridRows = DefaultRows,
        int gridColumns = DefaultColumns)
    {
        var result = new Dictionary<string, DashboardGridSlot>();
        var rows = ClampDimension(gridRows);
        var cols = ClampDimension(gridColumns);

        // Personalizado: la geometría no sale de una tabla de tramos sino de lo que el usuario colocó a
        // mano en Ajustes › Dashboard, así que hay que sanearla (recortes, solapes, secciones sin
        // posición guardada) antes de dársela a la vista — ver NormalizeCustom.
        if (style == Custom)
            return NormalizeCustom(orderedKeys, customLayout, rows, cols);

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
            result[orderedKeys[i]] = new DashboardGridSlot(i, 0, 1, cols);

        return result;
    }

    /// <summary>Recorta cualquier dimensión de rejilla al rango que el resto del sistema da por válido
    /// (1..<see cref="MaxGridDimension"/>). Un único sitio para la regla, en vez de repetir el mismo
    /// <c>Math.Clamp</c> en el motor, el ViewModel de Ajustes y el del Dashboard.</summary>
    public static int ClampDimension(int value) => Math.Clamp(value, 1, MaxGridDimension);

    /// <summary>Convierte la geometría guardada por el usuario
    /// (<see cref="Models.VaultSettings.DashboardCustomLayout"/>) en una colocación garantizada sin
    /// solapes y dentro de la rejilla, que es lo único que la vista sabe pintar bien.
    ///
    /// Hace falta porque esa geometría es la única del sistema que no la genera el propio motor: la
    /// escribe el editor de Ajustes, sobrevive a cambios de tamaño de la rejilla (reducir columnas deja
    /// fichas fuera de rango), a secciones añadidas en versiones nuevas (Habits no existía cuando se
    /// guardó el layout) y a un <c>settings.json</c> editado a mano. Las reglas, en orden:
    ///
    /// 1. Cada ficha con posición guardada se recorta a la rejilla y se queda donde está <i>si el hueco
    ///    sigue libre</i>. El orden de <paramref name="orderedKeys"/> (el de la lista de Ajustes) decide
    ///    quién gana un hueco disputado, igual que ya decide qué sección coge el tramo más prominente en
    ///    las plantillas.
    /// 2. Las que pierden el hueco, y las que nunca tuvieron posición, se recolocan por primer ajuste
    ///    conservando su tamaño (o el tamaño por defecto de su clase, <see cref="DefaultSpanFor"/>).
    /// 3. Si no cabe ninguna en las filas configuradas, se añaden filas por debajo — nunca se descarta ni
    ///    se solapa una sección: el Dashboard deriva su número de filas del contenido, así que una fila
    ///    extra se ve como tal y no como un layout roto.</summary>
    public static Dictionary<string, DashboardGridSlot> NormalizeCustom(
        IReadOnlyList<string> orderedKeys,
        IReadOnlyDictionary<string, int[]>? customLayout,
        int gridRows = DefaultRows,
        int gridColumns = DefaultColumns)
    {
        var rows = ClampDimension(gridRows);
        var cols = ClampDimension(gridColumns);
        var result = new Dictionary<string, DashboardGridSlot>();

        // Alto de trabajo con margen para el desbordamiento de la regla 3: en el peor caso cada sección
        // se lleva una fila propia por debajo de la rejilla configurada.
        var maxRows = rows + orderedKeys.Count;
        var occupied = new bool[maxRows, cols];
        var pending = new List<(string Key, int RowSpan, int ColumnSpan)>();

        foreach (var key in orderedKeys)
        {
            if (customLayout is not null && customLayout.TryGetValue(key, out var pos) && pos is { Length: >= 4 })
            {
                var r = Math.Clamp(pos[0], 0, rows - 1);
                var c = Math.Clamp(pos[1], 0, cols - 1);
                var rs = Math.Clamp(pos[2], 1, rows - r);
                var cs = Math.Clamp(pos[3], 1, cols - c);

                if (IsAreaFree(occupied, r, c, rs, cs))
                {
                    Occupy(occupied, r, c, rs, cs);
                    result[key] = new DashboardGridSlot(r, c, rs, cs);
                }
                else
                    pending.Add((key, rs, cs));
            }
            else
                pending.Add((key, 1, DefaultSpanFor(key, cols)));
        }

        foreach (var (key, rowSpan, colSpan) in pending)
        {
            var cs = Math.Clamp(colSpan, 1, cols);
            var rs = Math.Clamp(rowSpan, 1, rows);
            var slot = FirstFit(occupied, maxRows, cols, rs, cs);
            Occupy(occupied, slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan);
            result[key] = slot;
        }

        return result;
    }

    /// <summary>Primer hueco libre (arriba-izquierda, por filas) donde cabe un rectángulo de
    /// <paramref name="rowSpan"/>×<paramref name="colSpan"/>. El barrido llega hasta
    /// <paramref name="maxRows"/>, dimensionado por el llamante para que siempre haya filas vacías de
    /// sobra, así que siempre encuentra sitio.</summary>
    private static DashboardGridSlot FirstFit(bool[,] occupied, int maxRows, int cols, int rowSpan, int colSpan)
    {
        var rs = Math.Min(rowSpan, maxRows);
        var cs = Math.Min(colSpan, cols);

        for (var r = 0; r + rs <= maxRows; r++)
        {
            for (var c = 0; c + cs <= cols; c++)
            {
                if (IsAreaFree(occupied, r, c, rs, cs))
                    return new DashboardGridSlot(r, c, rs, cs);
            }
        }

        // Inalcanzable con un maxRows dimensionado como arriba; una fila propia al final antes que
        // devolver un solape si algún llamante futuro lo aprieta más.
        return new DashboardGridSlot(Math.Max(0, maxRows - rs), 0, rs, cs);
    }

    private static bool IsAreaFree(bool[,] occupied, int row, int column, int rowSpan, int columnSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
        {
            for (var c = column; c < column + columnSpan; c++)
            {
                if (r < 0 || c < 0 || r >= occupied.GetLength(0) || c >= occupied.GetLength(1)) return false;
                if (occupied[r, c]) return false;
            }
        }
        return true;
    }

    private static void Occupy(bool[,] occupied, int row, int column, int rowSpan, int columnSpan)
    {
        for (var r = row; r < row + rowSpan; r++)
            for (var c = column; c < column + columnSpan; c++)
                if (r >= 0 && c >= 0 && r < occupied.GetLength(0) && c < occupied.GetLength(1))
                    occupied[r, c] = true;
    }

    /// <summary>Si dos colocaciones comparten alguna celda. Lo usa el editor de Ajustes para decidir en
    /// vivo (mientras se arrastra) si el hueco bajo el puntero es válido, con la misma definición de
    /// "solape" que aplica <see cref="NormalizeCustom"/> al guardar.</summary>
    public static bool Overlaps(DashboardGridSlot a, DashboardGridSlot b)
        => a.Column < b.Column + b.ColumnSpan && b.Column < a.Column + a.ColumnSpan
        && a.Row < b.Row + b.RowSpan && b.Row < a.Row + a.RowSpan;

    /// <summary>Si <paramref name="candidate"/> cabe en la rejilla y no pisa a ninguna otra ficha
    /// (ignorando la que se está moviendo, <paramref name="ignoreKey"/>).</summary>
    public static bool CanPlace(
        IReadOnlyDictionary<string, DashboardGridSlot> placed,
        string? ignoreKey,
        DashboardGridSlot candidate,
        int gridRows,
        int gridColumns)
    {
        var rows = ClampDimension(gridRows);
        var cols = ClampDimension(gridColumns);

        if (candidate.Row < 0 || candidate.Column < 0 || candidate.RowSpan < 1 || candidate.ColumnSpan < 1) return false;
        if (candidate.Row + candidate.RowSpan > rows || candidate.Column + candidate.ColumnSpan > cols) return false;

        foreach (var (key, slot) in placed)
        {
            if (key == ignoreKey) continue;
            if (Overlaps(candidate, slot)) return false;
        }

        return true;
    }

    /// <summary>Tamaño natural (filas × columnas) de una plantilla — lo que ocupa su propia tabla de
    /// tramos. Al pasar de una plantilla a Personalizado, la rejilla del editor adopta este tamaño para
    /// que la conversión sea idéntica en pantalla en vez de recortar la plantilla a la rejilla que
    /// hubiera configurada antes.</summary>
    public static (int Rows, int Columns) NaturalSizeOf(string style)
        => Templates.TryGetValue(style, out var slots)
            ? (ClampDimension(slots.Max(s => s.Row + s.RowSpan)), DefaultColumns)
            : (ClampDimension(DefaultRows), DefaultColumns);

    /// <summary>Semilla de <see cref="Models.VaultSettings.DashboardCustomLayout"/> a partir de otra
    /// organización: convierte la geometría que <paramref name="fromStyle"/> daría a estas secciones en
    /// posiciones editables una a una. Es lo que hace que "personalizar" no empiece nunca de un lienzo en
    /// blanco — el usuario elige la plantilla que más se le parece y a partir de ahí mueve fichas.</summary>
    public static Dictionary<string, int[]> BuildCustomLayoutFrom(
        IReadOnlyList<string> orderedKeys,
        string fromStyle,
        int gridRows,
        int gridColumns)
    {
        var rows = ClampDimension(gridRows);
        var cols = ClampDimension(gridColumns);

        // Classic vertido en una rejilla de N columnas sería una única columna de filas a todo lo ancho:
        // correcto, pero deja al usuario sin nada que reorganizar. Se siembra igualmente tal cual — es
        // exactamente lo que la plantilla dice — y el botón "auto-organizar" del editor ofrece la
        // alternativa compacta.
        var slots = Compute(orderedKeys, fromStyle, null, rows, cols);
        var normalized = NormalizeCustom(
            orderedKeys,
            slots.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Row, kv.Value.Column, kv.Value.RowSpan, kv.Value.ColumnSpan }),
            rows,
            cols);

        return normalized.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.Row, kv.Value.Column, kv.Value.RowSpan, kv.Value.ColumnSpan });
    }

    /// <summary>Recoloca todas las secciones visibles por primer ajuste conservando su tamaño actual —
    /// el "auto-organizar" del editor, que compacta la rejilla hacia arriba-izquierda después de un rato
    /// moviendo fichas a mano.</summary>
    public static Dictionary<string, int[]> AutoArrange(
        IReadOnlyList<string> orderedKeys,
        IReadOnlyDictionary<string, int[]>? currentLayout,
        int gridRows,
        int gridColumns)
    {
        var rows = ClampDimension(gridRows);
        var cols = ClampDimension(gridColumns);
        var maxRows = rows + orderedKeys.Count;
        var occupied = new bool[maxRows, cols];
        var result = new Dictionary<string, int[]>();

        foreach (var key in orderedKeys)
        {
            var rs = 1;
            var cs = DefaultSpanFor(key, cols);
            if (currentLayout is not null && currentLayout.TryGetValue(key, out var pos) && pos is { Length: >= 4 })
            {
                rs = Math.Clamp(pos[2], 1, rows);
                cs = Math.Clamp(pos[3], 1, cols);
            }

            var slot = FirstFit(occupied, maxRows, cols, rs, cs);
            Occupy(occupied, slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan);
            result[key] = [slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan];
        }

        return result;
    }
}
