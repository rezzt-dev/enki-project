using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class EditorAttachmentsUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "enki-attachment-ui-" + Guid.NewGuid().ToString("N"));

    public EditorAttachmentsUiTests() => HeadlessApp.EnsureInitialized();
    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void EmbeddedDocument_HasClickableCard_AndManagerUsesRealReference()
    {
        var vault = new VaultService(new SettingsService(_directory));
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        File.WriteAllText(Path.Combine(vault.VaultPath, "documento.txt"), "contenido");
        var note = vault.CreateNote("Proyecto");
        vault.WriteContent(note, "![Documento](documento.txt)\n\n");
        var vm = new EditorViewModel(vault, note, () => { }, () => { });
        var view = new EditorView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            var editor = view.GetVisualDescendants().OfType<TextEditor>().First();
            editor.CaretOffset = editor.Document.TextLength;
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            var card = Assert.Single(editor.TextArea.TextView.GetVisualChildren().OfType<Button>());
            Assert.True(card.Height >= 48);
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(vm.IsAttachmentsOpen);
            Assert.Equal("documento.txt", vm.AttachmentPreview?.FileName);
            Assert.Single(vm.Attachments);
            vm.AttachmentAnnotation = "Comentario";
            vm.AddAttachmentAnnotationCommand.Execute(null);
            Assert.Contains("> Comentario", editor.Document.Text);
            vm.CloseAttachmentsCommand.Execute(null);
            Assert.Null(vm.AttachmentPreview);
        }
        finally { vm.Close(); window.Close(); }
    }

    [Theory]
    [InlineData(900, false)]
    [InlineData(1200, true)]
    public void Manager_PreviewFitsWindow_InBothThemes(int width, bool light)
    {
        var vault = new VaultService(new SettingsService(_directory));
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        File.WriteAllText(Path.Combine(vault.VaultPath, "Informe.txt"), "Conclusiones del proyecto\n\nRevisar resultados y próximos pasos.");
        var note = vault.CreateNote("Proyecto");
        vault.WriteContent(note, "![Informe](Informe.txt)\n![Grabación](reunion.mp3)\n![Documento](informe.pdf)");
        var vm = new EditorViewModel(vault, note, () => { }, () => { });
        vm.OpenAttachmentTarget("Informe.txt");
        var view = new NoteAttachmentsView { DataContext = vm };
        var card = new Border { Child = view, Classes = { "modal-card" }, MaxWidth = 1100 };
        var previousTheme = Application.Current!.RequestedThemeVariant;
        Application.Current.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;
        var window = new Window { Content = card, Width = width, Height = 800 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.True(view.Bounds.Width < width);
            Assert.True(view.Bounds.Height < 800);
            Assert.True(vm.AttachmentPreview!.IsCodeViewer);
            Assert.False(view.FindControl<TextBlock>("PdfLoadingHint")!.IsVisible);
            Assert.False(view.FindControl<TextBlock>("PdfErrorHint")!.IsVisible);
            Assert.False(view.FindControl<TextBlock>("SelectAttachmentHint")!.IsVisible);
            if (Environment.GetEnvironmentVariable("ENKI_GRAPH_PREVIEW") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var bitmap = new RenderTargetBitmap(new PixelSize(width, 800));
                bitmap.Render(card);
                bitmap.Save(Path.Combine(directory, $"attachments-{(light ? "light" : "dark")}.png"), PngBitmapEncoderOptions.Default);
            }
        }
        finally { vm.Close(); window.Close(); Application.Current!.RequestedThemeVariant = previousTheme; }
    }

    [Fact]
    public void Manager_WithNoAttachments_RendersEmptyState()
    {
        var vault = new VaultService(new SettingsService(_directory));
        vault.InitializeVault(Path.Combine(_directory, "vault"), seedWelcomeNote: false);
        var vm = new EditorViewModel(vault, vault.CreateNote("Vacia"), () => { }, () => { });
        var view = new NoteAttachmentsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 650 };
        try
        {
            window.Show();
            vm.ShowAttachmentsCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();
            Assert.Empty(vm.Attachments);
            Assert.False(vm.AddAttachmentAnnotationCommand.CanExecute(null));
            Assert.True(view.Bounds.Width > 0);
        }
        finally { vm.Close(); window.Close(); }
    }
}
