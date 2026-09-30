using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre el reparto de colores del Calendario (docs/calendar-feature/
/// 07-filtros-busqueda-y-capas.md §1b): los tres colores reservados de los roles core de Kanban, los
/// acentos automáticos de columnas personalizadas y listas de Polar (que nunca pueden ser rojo/amarillo/
/// verde) y los colores elegidos a mano, que viven en los ajustes y no en la columna ni en la lista.</summary>
public class CalendarLayerColorServiceTests
{
    private static readonly string[] KanbanRoleColors =
        [CalendarLayerPalette.BacklogColorHex, CalendarLayerPalette.InProgressColorHex, CalendarLayerPalette.DoneColorHex];

    private sealed class Harness : IDisposable
    {
        public string Directory { get; }
        public SettingsService Settings { get; }
        public KanbanService Kanban { get; }
        public PolarTaskService Tasks { get; }
        public PolarAuthService Auth { get; }
        public CalendarLayerColorService Colors { get; }

        public Harness(bool polarLinked = false)
        {
            Directory = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));
            Settings = new SettingsService(Directory);
            Kanban = new KanbanService(Directory);
            Auth = new PolarAuthService(Settings, baseUrl: "https://test.example", anonKey: "test-key");
            if (polarLinked)
            {
                Settings.Settings.PolarLinked = true;
                Settings.Settings.PolarUserId = "user-1";
            }
            Tasks = new PolarTaskService(Directory);
            Colors = new CalendarLayerColorService(Kanban, Tasks, Auth, Settings);
        }

        public KanbanColumn Column(CoreColumnRole role) => Kanban.DefaultBoard.Columns.First(c => c.Role == role);

        public KanbanColumn AddCustomColumn(string title)
        {
            var column = new KanbanColumn { Id = Guid.NewGuid().ToString("N"), Title = title };
            Kanban.DefaultBoard.Columns.Add(column);
            Kanban.Save();
            return column;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private static string ColorOf(IReadOnlyDictionary<string, string> map, KanbanColumn column) =>
        CalendarLayerColorService.Resolve(map, CalendarLayerColorService.KanbanColumnColorKey(column.Id));

    // ===================== Colores reservados de Kanban =====================

    [Fact]
    public void BuildMap_CoreColumns_UseReservedRoleColors()
    {
        using var h = new Harness();

        var map = h.Colors.BuildMap();

        Assert.Equal(CalendarLayerPalette.BacklogColorHex, ColorOf(map, h.Column(CoreColumnRole.Backlog)));
        Assert.Equal(CalendarLayerPalette.InProgressColorHex, ColorOf(map, h.Column(CoreColumnRole.InProgress)));
        Assert.Equal(CalendarLayerPalette.DoneColorHex, ColorOf(map, h.Column(CoreColumnRole.Done)));
    }

    [Fact]
    public void BuildMap_CustomColumn_GetsAccentNeverAReservedRoleColor()
    {
        using var h = new Harness();
        var column = h.AddCustomColumn("Bloqueadas");

        var color = ColorOf(h.Colors.BuildMap(), column);

        Assert.Contains(color, CalendarLayerPalette.AccentPool);
        Assert.DoesNotContain(color, KanbanRoleColors);
    }

    // ===================== Listas de Polar =====================

    [Fact]
    public void BuildMap_PolarLists_GetDistinctAccentsIgnoringTheirOwnColor()
    {
        using var h = new Harness(polarLinked: true);
        // Polar crea todas las listas con el mismo morado por defecto: ese es justo el motivo de repartir
        // colores en el calendario en vez de reutilizar PolarTaskList.Color.
        var lists = Enumerable.Range(1, 5)
            .Select(i => h.Tasks.CreateList($"Lista {i}", "ic_list", "#7F52FF", false))
            .ToList();

        var map = h.Colors.BuildMap();
        var colors = lists.Select(l => CalendarLayerColorService.Resolve(map, l.Id)).ToList();

        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.All(colors, c => Assert.Contains(c, CalendarLayerPalette.AccentPool));
        Assert.All(colors, c => Assert.DoesNotContain(c, KanbanRoleColors));
    }

    [Fact]
    public void BuildMap_CustomColumnsAndPolarLists_ShareOnePoolSoTheyDoNotCollide()
    {
        using var h = new Harness(polarLinked: true);
        var column = h.AddCustomColumn("Bloqueadas");
        var list = h.Tasks.CreateList("Lista", "ic_list", "#7F52FF", false);

        var map = h.Colors.BuildMap();

        Assert.NotEqual(ColorOf(map, column), CalendarLayerColorService.Resolve(map, list.Id));
    }

    [Fact]
    public void BuildMap_IsDeterministicAcrossInstances()
    {
        using var h = new Harness(polarLinked: true);
        h.AddCustomColumn("Bloqueadas");
        h.Tasks.CreateList("Lista", "ic_list", "#7F52FF", false);

        var first = h.Colors.BuildMap();
        var second = new CalendarLayerColorService(
            h.Kanban, h.Tasks, h.Auth, h.Settings).BuildMap();

        Assert.Equal(first, second);
    }

    // ===================== Color elegido a mano =====================

    [Fact]
    public void SetColor_OverridesTheAutomaticColorAndPersists()
    {
        using var h = new Harness();
        var column = h.Column(CoreColumnRole.Backlog);
        var key = CalendarLayerColorService.KanbanColumnColorKey(column.Id);

        Assert.True(h.Colors.SetColor(key, "#123abc"));

        Assert.Equal("#123ABC", ColorOf(h.Colors.BuildMap(), column));
        Assert.True(h.Colors.HasCustomColor(key));
        Assert.Equal("#123ABC", new SettingsService(h.Directory).Settings.CalendarLayerColors[key]);
    }

    [Fact]
    public void SetColor_DoesNotTouchTheKanbanColumnOrThePolarList()
    {
        using var h = new Harness(polarLinked: true);
        var column = h.Column(CoreColumnRole.Backlog);
        var list = h.Tasks.CreateList("Lista", "ic_list", "#7F52FF", false);
        var listColorBefore = list.Color;

        h.Colors.SetColor(CalendarLayerColorService.KanbanColumnColorKey(column.Id), "#123ABC");
        h.Colors.SetColor(list.Id, "#ABCDEF");

        // El color es una preferencia del calendario: PolarTaskList.Color viaja a Supabase, así que
        // reescribirlo cambiaría la lista en todos los dispositivos por un ajuste de esta pantalla.
        Assert.Equal(listColorBefore, list.Color);
        Assert.Equal("Lista", list.Title);
    }

    [Fact]
    public void SetColor_InvalidHex_IsRejectedAndChangesNothing()
    {
        using var h = new Harness();
        var key = CalendarLayerColorService.KanbanColumnColorKey(h.Column(CoreColumnRole.Backlog).Id);

        Assert.False(h.Colors.SetColor(key, "no-es-un-color"));

        Assert.False(h.Colors.HasCustomColor(key));
        Assert.Equal(CalendarLayerPalette.BacklogColorHex, CalendarLayerColorService.Resolve(h.Colors.BuildMap(), key));
    }

    [Fact]
    public void ResetColor_GoesBackToTheAutomaticColor()
    {
        using var h = new Harness();
        var column = h.Column(CoreColumnRole.Done);
        var key = CalendarLayerColorService.KanbanColumnColorKey(column.Id);
        h.Colors.SetColor(key, "#123ABC");

        h.Colors.ResetColor(key);

        Assert.False(h.Colors.HasCustomColor(key));
        Assert.Equal(CalendarLayerPalette.DoneColorHex, ColorOf(h.Colors.BuildMap(), column));
    }

    [Fact]
    public void SetColor_RaisesColorsChangedOnceAndOnlyOnARealChange()
    {
        using var h = new Harness();
        var key = CalendarLayerColorService.KanbanColumnColorKey(h.Column(CoreColumnRole.Backlog).Id);
        var raised = 0;
        h.Colors.ColorsChanged += () => raised++;

        h.Colors.SetColor(key, "#123ABC");
        h.Colors.SetColor(key, "#123abc");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void BuildMap_AnAutomaticColorNeverRepeatsOneChosenByHand()
    {
        using var h = new Harness(polarLinked: true);
        var pinned = h.Tasks.CreateList("Fijada", "ic_list", "#7F52FF", false);
        var others = Enumerable.Range(1, 3)
            .Select(i => h.Tasks.CreateList($"Lista {i}", "ic_list", "#7F52FF", false))
            .ToList();
        // Se fija a mano justo el color que la paleta le había repartido a otra lista.
        var taken = CalendarLayerColorService.Resolve(h.Colors.BuildMap(), others[0].Id);
        h.Colors.SetColor(pinned.Id, taken);

        var map = h.Colors.BuildMap();

        Assert.Equal(taken, CalendarLayerColorService.Resolve(map, pinned.Id));
        Assert.All(others, l => Assert.NotEqual(taken, CalendarLayerColorService.Resolve(map, l.Id)));
    }

    // ===================== La paleta en sí =====================

    /// <summary>Distancia perceptual aproximada entre dos colores (la "redmean" de Thiadmer Riemersma,
    /// el mismo atajo que usan los selectores de color para ordenar por parecido): más fiable que comparar
    /// canales RGB sueltos y sin necesitar una conversión a Lab completa para lo que se comprueba aquí.</summary>
    private static double PerceptualDistance(string hexA, string hexB)
    {
        var (r1, g1, b1) = Channels(hexA);
        var (r2, g2, b2) = Channels(hexB);
        var meanRed = (r1 + r2) / 2.0;
        double dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return Math.Sqrt((2 + meanRed / 256) * dr * dr + 4 * dg * dg + (2 + (255 - meanRed) / 256) * db * db);

        static (int R, int G, int B) Channels(string hex) =>
            (Convert.ToInt32(hex.Substring(1, 2), 16),
             Convert.ToInt32(hex.Substring(3, 2), 16),
             Convert.ToInt32(hex.Substring(5, 2), 16));
    }

    /// <summary>El requisito real no es "que el hex sea distinto" sino "que se distingan a simple vista":
    /// un rosa y el rojo de "pendientes" son dos hex distintos y aun así se confunden en una pastilla del
    /// mes. Este test fija esa distancia para que ampliar la paleta no reintroduzca el problema.
    ///
    /// El listón es más alto con los tres colores de rol de Kanban que con el de recordatorios porque
    /// rojo/ámbar/verde significan un estado ("pendiente", "en proceso", "realizada"): confundir una lista
    /// de Polar con uno de ellos hace leer mal el calendario, mientras que confundirla con recordatorios
    /// solo cuesta saber de dónde viene el item. De ahí el doble de margen para unos y el mismo margen que
    /// se guardan entre sí los acentos para el otro.</summary>
    [Fact]
    public void AccentPool_StaysClearOfTheColorsReservedForKanbanAndReminders()
    {
        string[] kanbanRoleColors =
        [
            CalendarLayerPalette.BacklogColorHex, CalendarLayerPalette.InProgressColorHex,
            CalendarLayerPalette.DoneColorHex
        ];

        var closestPair = CalendarLayerPalette.AccentPool
            .SelectMany((a, i) => CalendarLayerPalette.AccentPool.Skip(i + 1).Select(b => PerceptualDistance(a, b)))
            .Min();

        Assert.All(CalendarLayerPalette.AccentPool, accent =>
        {
            Assert.All(kanbanRoleColors, role => Assert.True(
                PerceptualDistance(accent, role) > closestPair * 2,
                $"{accent} está demasiado cerca del color de estado {role}"));

            Assert.True(
                PerceptualDistance(accent, CalendarItemViewModel.ReminderLayerColorHex) > closestPair,
                $"{accent} está demasiado cerca del color de recordatorios");
        });
    }

    [Fact]
    public void AccentPool_HasNoDuplicatesAndIsNormalizedUppercaseHex()
    {
        Assert.Equal(CalendarLayerPalette.AccentPool.Count, CalendarLayerPalette.AccentPool.Distinct().Count());
        Assert.All(CalendarLayerPalette.AccentPool, c => Assert.Equal(c, HexColorUtility.NormalizeHex(c)));
        // La rejilla del selector ofrece los tres roles de Kanban, el índigo de recordatorios y los acentos.
        Assert.Equal(CalendarLayerPalette.AccentPool.Count + 4, CalendarLayerPalette.PickerSwatches.Count);
        Assert.All(CalendarLayerPalette.PickerSwatches, c => Assert.Equal(c, HexColorUtility.NormalizeHex(c)));
    }

    // ===================== Integración con la agregación =====================

    [Fact]
    public void BuildItems_KanbanCard_TakesItsColumnColorWithoutChangingLayerColorHex()
    {
        using var h = new Harness();
        var due = DateTime.UtcNow.Date.AddDays(1).AddHours(10);
        var column = h.Column(CoreColumnRole.InProgress);
        column.Cards.Add(new KanbanCard { Id = "card-1", Title = "Tarjeta", DueDateUtc = due });
        h.Kanban.Save();

        var reminders = new ReminderService(h.Directory);
        var aggregation = new CalendarAggregationService(h.Kanban, h.Tasks, reminders, h.Auth, h.Colors);

        var item = aggregation.BuildItems(due.AddHours(-1), due.AddHours(1)).Single(i => i.SourceId == "card-1");

        Assert.Equal(CalendarLayerPalette.InProgressColorHex, item.EffectiveCalendarColorHex);
        Assert.Equal(CalendarLayerPalette.InProgressColorHex, item.ChipBackgroundColorHex);
        // El Dashboard sigue leyendo LayerColorHex: el color es solo del calendario.
        Assert.Equal(CalendarItemViewModel.KanbanLayerColorHex, item.LayerColorHex);
    }

    [Fact]
    public void BuildItems_WithoutAColorService_FallsBackToTheOriginalLayerColor()
    {
        using var h = new Harness();
        var due = DateTime.UtcNow.Date.AddDays(1).AddHours(10);
        h.Column(CoreColumnRole.Backlog).Cards.Add(new KanbanCard { Id = "card-1", Title = "Tarjeta", DueDateUtc = due });
        h.Kanban.Save();

        var reminders = new ReminderService(h.Directory);
        var aggregation = new CalendarAggregationService(h.Kanban, h.Tasks, reminders, h.Auth);

        var item = aggregation.BuildItems(due.AddHours(-1), due.AddHours(1)).Single(i => i.SourceId == "card-1");

        Assert.Equal(CalendarItemViewModel.KanbanLayerColorHex, item.EffectiveCalendarColorHex);
    }
}
