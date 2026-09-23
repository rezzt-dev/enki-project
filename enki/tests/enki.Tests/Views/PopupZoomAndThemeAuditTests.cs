using System;
using System.IO;
using System.Linq;
using Xunit;

namespace enki.Tests.Views;

/// <summary>Fase 6 (roadmap §11, "Tests de zoom y tema: verificar que popovers, diálogos y notificaciones
/// respeten <c>UiZoomPercent</c> y el tema activo") and gap 26. Fase 1 closed gap 26 by manually auditing
/// two invariants the zoom <c>ScaleTransform</c> (docs/funcionalidad-zoom-interfaz.md §9 punto 5) and the
/// single global <c>Style Selector="Popup"</c> in <c>Styles.axaml</c> depend on: every popover in the app
/// is a real Avalonia <c>Popup</c> (never a <c>Flyout</c>, which resolves its own overlay independent of
/// that shared style). T2 añade una excepción deliberada: <c>QuickCaptureWindow</c> debe existir aunque la
/// principal esté oculta, y por eso declara su propio <c>LayoutTransformControl</c> con el zoom del vault.
/// Esa auditoría era manual y dejó sin prueba de regresión
/// behind it — this file turns it into one, scanning the real <c>.axaml</c> source tree the same way
/// <c>RefreshLocalizedLabelsCoverageTests</c> scans <c>.cs</c> files, so a future <c>&lt;Window&gt;</c> or
/// <c>&lt;Flyout&gt;</c> added to a view fails the build instead of silently falling outside the
/// zoom/theme system.</summary>
public class PopupZoomAndThemeAuditTests
{
    private static readonly string ViewsDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "views"));

    private static readonly string StylesDirectory = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "enki", "styles"));

    [Fact]
    public void OnlyShellAndQuickCapture_DeclareRealWindows_AndBothApplyZoom()
    {
        var windowFiles = Directory.EnumerateFiles(ViewsDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("<Window", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(["MainWindow.axaml", "QuickCaptureWindow.axaml"], windowFiles.OrderBy(name => name));

        foreach (var windowFile in windowFiles)
        {
            var content = File.ReadAllText(Path.Combine(ViewsDirectory, "shell", windowFile!));
            Assert.Contains("LayoutTransformControl", content, StringComparison.Ordinal);
            Assert.Contains("UiZoomScale", content, StringComparison.Ordinal);
        }
    }

    /// <summary>A <c>Flyout</c> renders through its own popup mechanism, not the shared
    /// <c>Style Selector="Popup"</c> in <c>Styles.axaml</c> that forces <c>ShouldUseOverlayLayer="True"</c>
    /// — so a <c>Flyout</c> anywhere in the app would silently skip the fix that makes popovers scale with
    /// <c>UiZoomPercent</c>. Every dropdown/menu/popover in Enki uses a real <c>Popup</c>-backed control
    /// instead (own-rolled dropdowns, <c>ComboBox</c>, context menus).</summary>
    [Fact]
    public void NoView_UsesAFlyout()
    {
        var flyoutFiles = Directory.EnumerateFiles(ViewsDirectory, "*.axaml", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("<Flyout", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(flyoutFiles);
    }

    /// <summary>Fixes the actual mechanism gap 26 relies on: the global <c>Popup</c> style must keep
    /// forcing <c>ShouldUseOverlayLayer="True"</c> so every popover renders above (and scales with) the
    /// zoomed root instead of in its own unscaled native overlay window.</summary>
    [Fact]
    public void GlobalPopupStyle_ForcesOverlayLayer()
    {
        var stylesPath = Path.Combine(StylesDirectory, "Styles.axaml");
        var content = File.ReadAllText(stylesPath);

        var popupStyleIndex = content.IndexOf("Selector=\"Popup\"", StringComparison.Ordinal);
        Assert.True(popupStyleIndex >= 0, "Styles.axaml no longer declares a global Popup style.");

        var styleBlockEnd = content.IndexOf("</Style>", popupStyleIndex, StringComparison.Ordinal);
        var styleBlock = content[popupStyleIndex..styleBlockEnd];
        Assert.Contains("ShouldUseOverlayLayer", styleBlock, StringComparison.Ordinal);
        Assert.Contains("\"True\"", styleBlock, StringComparison.Ordinal);
    }
}
