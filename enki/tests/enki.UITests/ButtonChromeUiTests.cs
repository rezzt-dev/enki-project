using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Xunit;

namespace enki.UITests;

/// <summary>El ControlTheme de Fluent pinta ButtonBackgroundPointerOver/Pressed sobre el ContentPresenter
/// de la plantilla, por encima del <c>{TemplateBinding Background}</c> del estado normal: sin el
/// neutralizado de <c>Styles.axaml</c> (§7.2, "Chrome de Fluent en botones") cualquier botón que no
/// redefina a mano el fondo de su ContentPresenter en hover/pulsado sale con un recuadro gris ajeno al
/// diseño — el bug visible en los botones de texto tipo "IR AL TABLERO" o "EDITAR".
///
/// Los tests fijan las dos mitades de la invariante: en hover/pulsado el fondo del ContentPresenter es el
/// del propio control, y las clases que sí declaran un fondo de hover propio (nav-item, pill, icon-btn…)
/// lo conservan pese a que el neutralizado se declare antes que ellas en el fichero.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class ButtonChromeUiTests
{
    public ButtonChromeUiTests() => HeadlessApp.EnsureInitialized();

    /// <summary>Las transiciones (BrushTransition sobre Background) se anulan a mano: si no, justo después
    /// de activar la pseudo-clase el ContentPresenter sigue devolviendo el color de reposo mientras la
    /// animación arranca, y el assert leería un valor intermedio en vez del destino del estilo.</summary>
    private static (T Control, ContentPresenter Presenter) Realize<T>(string classes) where T : ContentControl, new()
    {
        var control = new T { Content = "PULSAR" };
        foreach (var c in classes.Split(' ')) control.Classes.Add(c);

        var window = new Window { Content = control, Width = 400, Height = 200 };
        window.Show();

        var presenter = control.GetVisualDescendants().OfType<ContentPresenter>().First();
        control.Transitions = null;
        presenter.Transitions = null;
        return (control, presenter);
    }

    private static void SetState(TemplatedControl control, string pseudoClass)
        => ((IPseudoClasses)control.Classes).Set(pseudoClass, true);

    private static Color? ColorOf(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static Color ResourceColor(StyledElement host, string key)
    {
        Assert.True(host.TryFindResource(key, out var value), $"Recurso no encontrado: {key}");
        return ColorOf(value as IBrush) ?? throw new Xunit.Sdk.XunitException($"{key} no es un brush sólido");
    }

    [Theory]
    [InlineData(":pointerover")]
    [InlineData(":pressed")]
    public void AGhostButton_KeepsATransparentBackground_AndItsOwnForeground(string state)
    {
        var (button, presenter) = Realize<Button>("ghost");
        var restingForeground = ColorOf(presenter.Foreground);

        SetState(button, state);

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(restingForeground, ColorOf(presenter.Foreground));
    }

    [Theory]
    [InlineData("card", ":pointerover")]
    [InlineData("ghost-card", ":pointerover")]
    [InlineData("note-card", ":pointerover")]
    [InlineData("file-card", ":pointerover")]
    [InlineData("kanban-card", ":pointerover")]
    [InlineData("date-picker-trigger", ":pointerover")]
    [InlineData("link-mode-toggle", ":pointerover")]
    [InlineData("ghost modal-cancel", ":pointerover")]
    [InlineData("pdf-page-thumb", ":pointerover")]
    [InlineData("card", ":pressed")]
    [InlineData("note-card", ":pressed")]
    [InlineData("kanban-card", ":pressed")]
    [InlineData("dropdown-item", ":pressed")]
    [InlineData("pill", ":pressed")]
    [InlineData("section-header", ":pressed")]
    public void ClassesWithoutTheirOwnHoverFill_TakeTheBackgroundFromTheControl(string classes, string state)
    {
        var (button, presenter) = Realize<Button>(classes);

        SetState(button, state);

        Assert.Equal(ColorOf(button.Background), ColorOf(presenter.Background));
    }

    [Theory]
    [InlineData("calendar-chip", ":pointerover")]
    [InlineData("calendar-agenda-card", ":pointerover")]
    [InlineData("calendar-overflow-trigger", ":pointerover")]
    [InlineData("calendar-chip", ":pressed")]
    [InlineData("calendar-chip", ":checked")]
    [InlineData("calendar-agenda-card", ":checked")]
    [InlineData("calendar-overflow-trigger", ":checked")]
    public void ToggleButtons_FollowTheSameRule_CheckedIncluded(string cssClass, string state)
    {
        var (toggle, presenter) = Realize<ToggleButton>(cssClass);

        SetState(toggle, state);

        Assert.Equal(ColorOf(toggle.Background), ColorOf(presenter.Background));
    }

    [Fact]
    public void ATagChip_KeepsItsOwnCheckedFill()
    {
        var (toggle, presenter) = Realize<ToggleButton>("tag-chip");

        SetState(toggle, ":checked");

        Assert.Equal(ResourceColor(toggle, "InkBrush"), ColorOf(presenter.Background));
    }

    [Theory]
    [InlineData("nav-item", "Ink05")]
    [InlineData("pill", "InkBrush")]
    [InlineData("icon-btn", "Ink10")]
    [InlineData("icon-btn icon-btn-danger", "DangerMutedBrush")]
    [InlineData("dropdown-item", "Ink05")]
    [InlineData("skill-card", "PanelBackgroundHoverBrush")]
    public void ClassesThatDeclareTheirOwnHoverFill_StillWinOverTheReset(string classes, string resourceKey)
    {
        var (button, presenter) = Realize<Button>(classes);

        SetState(button, ":pointerover");

        Assert.Equal(ResourceColor(button, resourceKey), ColorOf(presenter.Background));
    }

    [Theory]
    [InlineData("nav-item", "Ink10")]
    [InlineData("icon-btn", "Ink20")]
    public void ClassesThatDeclareTheirOwnPressedFill_StillWinOverTheReset(string classes, string resourceKey)
    {
        var (button, presenter) = Realize<Button>(classes);

        SetState(button, ":pressed");

        Assert.Equal(ResourceColor(button, resourceKey), ColorOf(presenter.Background));
    }
}
