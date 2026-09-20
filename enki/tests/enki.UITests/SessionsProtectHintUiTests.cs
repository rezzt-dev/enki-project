using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using enki.Controls;
using enki.Localization;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>El aviso del diálogo de crear una sesión protegida
/// (<c>Sessions_CreateDialog_ProtectHint</c>) pasó de una línea a tres al tener que decir, además de que se
/// cifra el contenido, qué sigue siendo legible en disco (.docs/auditoria-seguridad-y-fallos-criticos.md,
/// SEC-05). Es un texto traducido a 7 idiomas dentro de un <c>Border.modal-card</c> de ancho fijo, así que
/// lo que hay que comprobar no es que "se vea bien" sino que ninguna traducción se salga de la tarjeta ni
/// haga crecer el diálogo por encima de una ventana pequeña. Se mide el XAML real.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class SessionsProtectHintUiTests
{
    /// <summary>La altura que el diálogo no debe superar: una ventana de 720 px de alto, que es la pantalla
    /// más pequeña razonable en un portátil, menos un margen para la barra de título propia.</summary>
    private const double MaxDialogHeight = 640;

    private const double ModalCardMaxWidth = 380;

    [Theory]
    [InlineData("es-ES")]
    [InlineData("es-419")]
    [InlineData("en-US")]
    [InlineData("en-GB")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    public void ProtectHint_FitsTheCreateDialog_InEveryLanguage(string culture)
    {
        HeadlessApp.EnsureInitialized();
        var previousLanguage = LocalizationService.Instance.CurrentCulture.Name;
        Window? window = null;
        try
        {
            LocalizationService.Instance.SetLanguage(culture);

            // El contenido del diálogo vive dentro de un ModalOverlayHost, cuyo Popup no se puede abrir en
            // headless: se saca el contenido y se monta directo, que es lo que ya hace
            // SessionsGraphFiltersUiTests con el popover del grafo.
            var view = new SessionsView();
            var card = view.GetLogicalDescendants().OfType<ModalOverlayHost>()
                .Select(h => h.Content)
                .OfType<Border>()
                .First(b => b.Classes.Contains("modal-card"));

            window = new Window { Content = card, Width = 420, Height = 720 };
            window.Show();
            window.GetLayoutManager()!.ExecuteLayoutPass();

            var hint = card.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Text == LocalizationService.Instance["Sessions_CreateDialog_ProtectHint"]);

            // Medido de verdad: el aviso ocupa 4 líneas (~70 px de alto) dentro de los 322 px de ancho
            // útiles de la tarjeta, y el diálogo entero se queda en 516–530 px según el idioma.
            var hintWidth = hint.Bounds.Width > 0 ? hint.Bounds.Width : hint.DesiredSize.Width;

            Assert.True(hintWidth <= ModalCardMaxWidth,
                $"[{culture}] el aviso mide {hintWidth} px de ancho dentro de una tarjeta de {ModalCardMaxWidth}.");
            Assert.True(card.DesiredSize.Height <= MaxDialogHeight,
                $"[{culture}] el diálogo mide {card.DesiredSize.Height} px de alto con el aviso dentro.");
            Assert.True(card.DesiredSize.Width <= ModalCardMaxWidth,
                $"[{culture}] el diálogo mide {card.DesiredSize.Width} px de ancho.");
        }
        finally
        {
            window?.Close();
            LocalizationService.Instance.SetLanguage(previousLanguage);
        }
    }
}
