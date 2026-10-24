using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.VisualTree;
using Material.Icons;
using Material.Icons.Avalonia;
using Xunit;

namespace enki.UITests;

/// <summary>El ControlTheme de Fluent pinta ButtonBackgroundPointerOver/Pressed sobre el ContentPresenter
/// de la plantilla, por encima del <c>{TemplateBinding Background}</c> del estado normal: sin el
/// neutralizado de <c>Styles.axaml</c> (§7.2, "Chrome de Fluent en botones") cualquier botón que no
/// redefina a mano el fondo de su ContentPresenter en hover/pulsado sale con un recuadro gris ajeno al
/// diseño — el bug visible en los botones de texto tipo "IR AL TABLERO" o "EDITAR".
///
/// Los tests fijan las tres mitades de la invariante: en hover/pulsado el fondo del ContentPresenter es el
/// del propio control, y las clases que sí declaran un fondo de hover propio (nav-item, pill, icon-btn…)
/// lo conservan pese a que el neutralizado se declare antes que ellas en el fichero; y los botones que
/// son solo un icono y no dibujan contorno (icon-btn, y el raíl plegado) no lo declaran a propósito:
/// resaltan el icono y nada más (ver el comentario de esa familia en Styles.axaml).</summary>
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

    /// <summary>Botón de icono de verdad: un <see cref="MaterialIcon"/> como contenido, que es sobre lo
    /// que resalta el estado — el fondo se queda en el del propio control.</summary>
    private static (Button Button, ContentPresenter Presenter, MaterialIcon Icon) RealizeIconButton(string classes)
    {
        var icon = new MaterialIcon { Kind = MaterialIconKind.Check, Width = 14, Height = 14 };
        var button = new Button { Content = icon };
        foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) button.Classes.Add(c);

        var window = new Window { Content = button, Width = 400, Height = 200 };
        window.Show();

        var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
        button.Transitions = null;
        presenter.Transitions = null;
        icon.Transitions = null;
        return (button, presenter, icon);
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
    [InlineData("icon-btn", ":pointerover")]
    [InlineData("icon-btn", ":pressed")]
    [InlineData("icon-btn icon-btn-danger", ":pointerover")]
    [InlineData("icon-btn icon-btn-danger", ":pressed")]
    [InlineData("nav-item collapsed", ":pointerover")]
    [InlineData("nav-item collapsed", ":pressed")]
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
    [InlineData("dropdown-item", "Ink05")]
    [InlineData("skill-card", "PanelBackgroundHoverBrush")]
    public void ClassesThatDeclareTheirOwnHoverFill_StillWinOverTheReset(string classes, string resourceKey)
    {
        var (button, presenter) = Realize<Button>(classes);

        SetState(button, ":pointerover");

        Assert.Equal(ResourceColor(button, resourceKey), ColorOf(presenter.Background));
    }

    [Fact]
    public void TheExpandedNavItem_StillDeclaresItsOwnPressedFill()
    {
        var (button, presenter) = Realize<Button>("nav-item");

        SetState(button, ":pressed");

        Assert.Equal(ResourceColor(button, "Ink10"), ColorOf(presenter.Background));
    }

    // ===================== Botones que son solo un icono =====================

    /// <summary>Sin contorno no hay caja: en hover y pulsado el fondo se queda en el del propio control
    /// (transparente) y lo único que cambia es el icono. El recuadro gris de 34x34 que pintaba antes leía
    /// como una caja ajena al botón.</summary>
    [Theory]
    [InlineData("icon-btn", ":pointerover")]
    [InlineData("icon-btn", ":pressed")]
    [InlineData("icon-btn icon-btn-danger", ":pointerover")]
    [InlineData("icon-btn icon-btn-danger", ":pressed")]
    public void AnIconOnlyButton_NeverPaintsABox(string classes, string state)
    {
        var (button, presenter, _) = RealizeIconButton(classes);

        SetState(button, state);

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
    }

    /// <summary>La contrapartida: el resalte tiene que verse en el icono, o el botón no daría ninguna
    /// señal. La acción destructiva se tiñe de danger en vez de aclararse.</summary>
    [Theory]
    [InlineData("icon-btn", "TextPrimaryBrush")]
    [InlineData("icon-btn icon-btn-danger", "DangerBrush")]
    public void AnIconOnlyButton_HighlightsItsIcon_OnHoverAndPress(string classes, string resourceKey)
    {
        var (button, _, icon) = RealizeIconButton(classes);
        Assert.NotEqual(ResourceColor(button, resourceKey), ColorOf(icon.Foreground));

        SetState(button, ":pointerover");

        Assert.Equal(ResourceColor(button, resourceKey), ColorOf(icon.Foreground));
    }

    /// <summary>El activo (conmutador Explorador/Árbol de Archivos, formato del editor, "hecho hoy"…) es
    /// el caso que más se ve en pantalla: mismo trato, sin fondo persistente.</summary>
    [Fact]
    public void AnActiveIconOnlyButton_KeepsTheBoxOut_AndTheIconHighlighted()
    {
        var (button, presenter, icon) = RealizeIconButton("icon-btn");

        button.Classes.Add("active");

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(ResourceColor(button, "TextPrimaryBrush"), ColorOf(icon.Foreground));
    }

    /// <summary>El raíl plegado sigue la misma regla que cualquier botón de icono: sin caja en hover,
    /// pulsado ni activo, con el icono aclarado. Desplegado se queda como estaba (relleno + barra de
    /// 3px), porque ahí la fila tiene etiqueta y el relleno es lo que marca la sección actual.</summary>
    [Theory]
    [InlineData(":pointerover")]
    [InlineData(":pressed")]
    public void ACollapsedNavItem_DropsTheBox_AndBrightensItsIcon(string state)
    {
        var (button, presenter, icon) = RealizeIconButton("nav-item collapsed");
        Assert.NotEqual(ResourceColor(button, "TextPrimaryBrush"), ColorOf(icon.Foreground));

        SetState(button, state);

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(ResourceColor(button, "TextPrimaryBrush"), ColorOf(icon.Foreground));
    }

    [Fact]
    public void AnActiveCollapsedNavItem_IsMarkedByItsIconOnly()
    {
        var (button, presenter, icon) = RealizeIconButton("nav-item collapsed");

        button.Classes.Add("active");

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(ResourceColor(button, "TextPrimaryBrush"), ColorOf(icon.Foreground));
    }

    /// <summary>Papelera se distingue por el rojo incluso en el raíl plegado: aclararlo a tinta plena al
    /// pasar el cursor le quitaría justo lo que lo hace reconocible.</summary>
    [Fact]
    public void ACollapsedDangerNavItem_StaysRedOnHover()
    {
        var (button, presenter, icon) = RealizeIconButton("nav-item collapsed");
        icon.Classes.Add("danger");
        var danger = ResourceColor(button, "DangerBrush");
        Assert.Equal(danger, ColorOf(icon.Foreground));

        SetState(button, ":pointerover");

        Assert.Equal(Colors.Transparent, ColorOf(presenter.Background));
        Assert.Equal(danger, ColorOf(icon.Foreground));
    }

    /// <summary>El check de exportar de las habilidades es el único botón de icono que no puede tomar su
    /// color del estilo base (alterna dos iconos según el estado), así que lo declara por clase: apagado en
    /// Ink40 y marcado en tinta plena, con el apagado aclarándose al pasar el cursor como cualquier otro.</summary>
    [Theory]
    [InlineData("skill-export-on", "InkBrush")]
    [InlineData("skill-export-off", "Ink40")]
    public void TheSkillExportCheckKeepsItsOwnColor(string cssClass, string resourceKey)
    {
        var (button, _, icon) = RealizeIconButton("icon-btn");

        icon.Classes.Add(cssClass);

        Assert.Equal(ResourceColor(button, resourceKey), ColorOf(icon.Foreground));
    }

    [Fact]
    public void TheClearedSkillExportCheck_BrightensOnHover()
    {
        var (button, _, icon) = RealizeIconButton("icon-btn");
        icon.Classes.Add("skill-export-off");

        SetState(button, ":pointerover");

        Assert.Equal(ResourceColor(button, "TextPrimaryBrush"), ColorOf(icon.Foreground));
    }

    /// <summary>El botón de plegar la barra pinta su hover sobre un Border propio (para alinearse con el
    /// logo), no sobre el ContentPresenter. La invariante que fijan estas reglas: la caja se pinta solo
    /// desplegado. Es un test del contrato del estilo — el Border de MainWindow.axaml lleva hoy un
    /// Background local que le gana a cualquier estilo, así que en la ventana real esa caja no llega a
    /// verse en ninguno de los dos estados (ver la nota en Styles.axaml).</summary>
    [Theory]
    [InlineData("icon-btn sidebar-toggle", "Ink10")]
    [InlineData("icon-btn sidebar-toggle collapsed", "Transparent")]
    public void TheSidebarToggleOnlyPaintsItsBoxWhileTheSidebarIsExpanded(string classes, string expected)
    {
        var box = new Border { Classes = { "sidebar-toggle-box" } };
        var button = new Button { Content = box };
        foreach (var c in classes.Split(' ', StringSplitOptions.RemoveEmptyEntries)) button.Classes.Add(c);
        var window = new Window { Content = button, Width = 400, Height = 200 };
        window.Show();

        // La caja lleva su propia BrushTransition (es el mismo Border del raíl): sin anularla, el assert
        // leería el valor de reposo mientras la animación arranca.
        box.Transitions = null;
        SetState(button, ":pointerover");

        var expectedColor = expected == "Transparent" ? Colors.Transparent : ResourceColor(button, expected);
        Assert.Equal(expectedColor, ColorOf(box.Background));
    }

}
