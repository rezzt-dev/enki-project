using System.Collections.Generic;
using System.Linq;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Cubre la mitad "personalizada" de <see cref="DashboardLayoutEngine"/>: la geometría que no
/// sale de una tabla de tramos escrita a mano sino de dónde haya colocado el usuario cada ficha en
/// Ajustes › Dashboard.
///
/// Es la parte que más red necesita, porque es la única cuya entrada no controla el propio motor: llega
/// de <c>settings.json</c> y puede venir de otra versión de la app (secciones que entonces no existían),
/// de una rejilla que desde entonces ha encogido, o de una edición a mano. La invariante que se afirma
/// una y otra vez aquí es siempre la misma — ninguna sección desaparece y no hay dos que compartan celda —
/// porque un solape no lo detecta ningún test de igualdad: se ve como dos tarjetas pintadas una encima de
/// otra, y solo mirando la pantalla.</summary>
public class DashboardCustomLayoutTests
{
    private static readonly string[] AllKeys =
        ["QuickAccess", "Today", "Vault", "Activity", "Sessions", "BoardTasks", "RecentNotes", "Habits"];

    private static void AssertNoOverlaps(IReadOnlyDictionary<string, DashboardGridSlot> slots)
    {
        var placed = slots.ToList();
        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                Assert.False(
                    DashboardLayoutEngine.Overlaps(placed[i].Value, placed[j].Value),
                    $"'{placed[i].Key}' {placed[i].Value} se solapa con '{placed[j].Key}' {placed[j].Value}");
            }
        }
    }

    [Fact]
    public void NormalizeCustom_KeepsEverySavedPosition_WhenTheyAllFitAndDoNotCollide()
    {
        var layout = new Dictionary<string, int[]>
        {
            ["QuickAccess"] = [0, 0, 1, 6],
            ["BoardTasks"] = [1, 0, 1, 4],
            ["Today"] = [1, 4, 1, 2]
        };

        var slots = DashboardLayoutEngine.NormalizeCustom(["QuickAccess", "BoardTasks", "Today"], layout, 4, 6);

        Assert.Equal(new DashboardGridSlot(0, 0, 1, 6), slots["QuickAccess"]);
        Assert.Equal(new DashboardGridSlot(1, 0, 1, 4), slots["BoardTasks"]);
        Assert.Equal(new DashboardGridSlot(1, 4, 1, 2), slots["Today"]);
    }

    [Fact]
    public void NormalizeCustom_AutoPlacesSectionsWithoutASavedPosition()
    {
        // "Habits" se añadió después de que existiera la organización personalizada: un vault guardado
        // antes no tiene posición para ella y aun así tiene que aparecer, sin pisar a nadie.
        var layout = new Dictionary<string, int[]> { ["QuickAccess"] = [0, 0, 1, 6] };

        var slots = DashboardLayoutEngine.NormalizeCustom(["QuickAccess", "Habits"], layout, 4, 6);

        Assert.Equal(2, slots.Count);
        Assert.Equal(new DashboardGridSlot(0, 0, 1, 6), slots["QuickAccess"]);
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void NormalizeCustom_ResolvesCollisions_InFavourOfTheHigherPrioritySection()
    {
        // Dos secciones guardadas en la misma celda (settings.json a mano, o un merge de dos máquinas):
        // gana la que va antes en la lista de Ajustes, la otra se recoloca.
        var layout = new Dictionary<string, int[]>
        {
            ["QuickAccess"] = [0, 0, 1, 3],
            ["Today"] = [0, 0, 1, 3]
        };

        var slots = DashboardLayoutEngine.NormalizeCustom(["QuickAccess", "Today"], layout, 4, 6);

        Assert.Equal(new DashboardGridSlot(0, 0, 1, 3), slots["QuickAccess"]);
        Assert.NotEqual(new DashboardGridSlot(0, 0, 1, 3), slots["Today"]);
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void NormalizeCustom_ClampsPositionsThatNoLongerFit_WhenTheGridShrinks()
    {
        // La rejilla pasa de 6 a 3 columnas: lo guardado apunta a columnas que ya no existen.
        var layout = new Dictionary<string, int[]>
        {
            ["QuickAccess"] = [0, 4, 1, 2],
            ["Today"] = [5, 0, 3, 6]
        };

        var slots = DashboardLayoutEngine.NormalizeCustom(["QuickAccess", "Today"], layout, 3, 3);

        Assert.All(slots.Values, slot =>
        {
            Assert.InRange(slot.Column, 0, 2);
            Assert.InRange(slot.Column + slot.ColumnSpan, 1, 3);
            Assert.True(slot.Row >= 0);
            Assert.True(slot.RowSpan >= 1);
        });
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void NormalizeCustom_NeverDropsASection_EvenWhenTheGridIsTooSmallForThemAll()
    {
        // Ocho secciones en una rejilla de 1×1: caben en filas por debajo de la configurada antes que
        // desaparecer — el Dashboard deriva sus filas del contenido, así que se ven todas.
        var slots = DashboardLayoutEngine.NormalizeCustom(AllKeys, new Dictionary<string, int[]>(), 1, 1);

        Assert.Equal(AllKeys.Length, slots.Count);
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void NormalizeCustom_IgnoresMalformedEntries_InsteadOfThrowing()
    {
        var layout = new Dictionary<string, int[]>
        {
            ["QuickAccess"] = [],
            ["Today"] = [1],
            ["Vault"] = [-5, -5, 0, 0]
        };

        var slots = DashboardLayoutEngine.NormalizeCustom(["QuickAccess", "Today", "Vault"], layout, 4, 6);

        Assert.Equal(3, slots.Count);
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void Compute_CustomWithoutAnySavedPosition_PacksInsteadOfStackingEverythingAtTheOrigin()
    {
        var slots = DashboardLayoutEngine.Compute(AllKeys, DashboardLayoutEngine.Custom, null, 4, 6);

        Assert.Equal(AllKeys.Length, slots.Count);
        AssertNoOverlaps(slots);
    }

    [Fact]
    public void CanPlace_RejectsOverlapsAndOutOfBounds_AndIgnoresTheTileBeingMoved()
    {
        var placed = new Dictionary<string, DashboardGridSlot>
        {
            ["QuickAccess"] = new(0, 0, 1, 6),
            ["Today"] = new(1, 0, 1, 2)
        };

        Assert.True(DashboardLayoutEngine.CanPlace(placed, "Today", new DashboardGridSlot(1, 2, 1, 2), 4, 6));
        Assert.False(DashboardLayoutEngine.CanPlace(placed, "Today", new DashboardGridSlot(0, 0, 1, 2), 4, 6));
        Assert.False(DashboardLayoutEngine.CanPlace(placed, "Today", new DashboardGridSlot(1, 5, 1, 2), 4, 6));
        Assert.False(DashboardLayoutEngine.CanPlace(placed, "Today", new DashboardGridSlot(3, 0, 2, 2), 4, 6));

        // Moverse encima de sí misma es legal: es lo que pasa al redimensionar sin cambiar de esquina.
        Assert.True(DashboardLayoutEngine.CanPlace(placed, "Today", new DashboardGridSlot(1, 0, 1, 4), 4, 6));
    }

    [Fact]
    public void BuildCustomLayoutFrom_ReproducesTheTemplateGeometryItStartsFrom()
    {
        var template = DashboardLayoutEngine.Compute(AllKeys, DashboardLayoutEngine.ModularGrid);
        var (rows, columns) = DashboardLayoutEngine.NaturalSizeOf(DashboardLayoutEngine.ModularGrid);

        var seeded = DashboardLayoutEngine.BuildCustomLayoutFrom(AllKeys, DashboardLayoutEngine.ModularGrid, rows, columns);

        foreach (var (key, slot) in template)
        {
            var position = seeded[key];
            Assert.Equal(new[] { slot.Row, slot.Column, slot.RowSpan, slot.ColumnSpan }, position);
        }
    }

    [Fact]
    public void AutoArrange_PacksTowardsTheTopLeft_KeepingEverySize()
    {
        var layout = new Dictionary<string, int[]>
        {
            ["QuickAccess"] = [3, 0, 1, 6],
            ["Today"] = [2, 4, 1, 2],
            ["Vault"] = [1, 2, 1, 2]
        };

        var arranged = DashboardLayoutEngine.AutoArrange(["QuickAccess", "Today", "Vault"], layout, 4, 6);

        Assert.Equal([0, 0, 1, 6], arranged["QuickAccess"]);
        Assert.Equal([1, 0, 1, 2], arranged["Today"]);
        Assert.Equal([1, 2, 1, 2], arranged["Vault"]);
    }

    [Theory]
    [InlineData("QuickAccess", 6, 3)]
    [InlineData("Today", 6, 2)]
    [InlineData("QuickAccess", 1, 1)]
    [InlineData("Today", 4, 2)]
    public void DefaultSpanFor_GivesWideSectionsHalfTheGridAndCompactOnesAThird(string key, int columns, int expected)
        => Assert.Equal(expected, DashboardLayoutEngine.DefaultSpanFor(key, columns));
}
