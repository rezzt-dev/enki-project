using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Conduce el selector de plantillas sobre el XAML real de <see cref="NotesView"/>, no sobre el
/// viewmodel: sus filas se enlazan con rutas del tipo
/// <c>#Root.((vm:NotesViewModel)DataContext).CreateNoteFromTemplateCommand</c>, que compilan siempre y solo
/// fallan —en silencio— al ejecutarse. Es justo el hueco que los tests de viewmodel no pueden cubrir.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class NotesTemplatePickerUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultDir;

    public NotesTemplatePickerUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultDir = Path.Combine(_tempDir, "vault");
        Directory.CreateDirectory(_vaultDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private (NotesView View, NotesViewModel ViewModel, Window Window) CreateView()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.SetVaultPath(_vaultDir);
        var session = new SessionService(vault);
        var viewModel = new NotesViewModel(vault, session, settingsService: settings,
            templateService: new NoteTemplateService(vault), vaultSettingsService: new VaultSettingsService(vault));

        var view = new NotesView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        return (view, viewModel, window);
    }

    private void WriteTemplate(string fileName, string content)
    {
        var directory = Path.Combine(_vaultDir, ".enki", "templates");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    [Fact]
    public void TemplateRow_CreatesTheNoteFromTheRealBinding()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\n# {{title}}\n");
        var (_, vm, window) = CreateView();
        try
        {
            vm.ShowTemplatePickerCommand.Execute(null);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            // La primera de las tres acciones de la fila (crear / editar / eliminar) en orden visual es la
            // que crea la nota; las otras dos se comprueban por el viewmodel en NotesViewModelTemplatesTests.
            var row = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.DataContext is NoteTemplateItemViewModel);
            Assert.NotNull(row);

            row!.Command!.Execute(row.CommandParameter);
            window.GetLayoutManager()!.ExecuteLayoutPass();

            Assert.False(vm.IsTemplatePickerOpen);
            var created = Assert.Single(Directory.EnumerateFiles(_vaultDir, "*.md"));
            Assert.Equal("Diario.md", Path.GetFileName(created));
            Assert.Equal("# Diario\n", File.ReadAllText(created));
            Assert.Equal(created, vm.ActiveEditor?.Note.FullPath);
        }
        finally
        {
            window.Close();
        }
    }
}
