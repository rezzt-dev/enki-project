using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.Controls;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>El diálogo de conflicto con un cambio externo al vault, ya montado sobre una ventana real
/// (.docs/project-info/cambios-externos-en-el-vault.md §6). Aquí se comprueba justo lo que las pruebas de
/// viewmodel no pueden: que el diálogo se abre de verdad sobre la ventana y que <c>Esc</c> —cuyo
/// <c>KeyBinding</c> vive dentro del popup, no en la vista— equivale a "decidir luego".</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class EditorExternalConflictUiTests
{
    private static (EditorView View, EditorViewModel Vm, NoteFile Note, VaultService Vault) CreateEditor(string directory)
    {
        var settings = new SettingsService(directory);
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(directory, "vault"), seedWelcomeNote: false);
        var note = vault.CreateNote("Nota en conflicto");
        var vm = new EditorViewModel(vault, note, () => { }, () => { }, settingsService: settings);
        return (new EditorView { DataContext = vm }, vm, note, vault);
    }

    /// <summary>Provoca un conflicto real: hay texto sin guardar y el archivo cambia por detrás.</summary>
    private static void RaiseConflict(EditorViewModel vm, NoteFile note)
    {
        vm.Document.Text = "# Nota en conflicto\n\nEditado en Enki.";
        vm.OnTextChanged(vm.Document.Text);
        File.WriteAllText(note.FullPath, "# Nota en conflicto\n\nDesde otro editor.");
        vm.HandleExternalChange(new VaultExternalChange(note.FullPath, VaultExternalChangeKind.Modified));
    }

    [Fact]
    public void ConflictDialog_OpensOverTheWindow_AndEscapePostponesTheDecision()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
        Window? window = null;
        var previousZen = EditorPreferences.IsZenMode;
        try
        {
            EditorPreferences.IsZenMode = false;
            var (view, vm, note, _) = CreateEditor(directory);
            window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();

            RaiseConflict(vm, note);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var dialog = view.GetVisualDescendants().OfType<ModalOverlayHost>()
                .Single(h => h.IsOpen && ReferenceEquals(h.DataContext, vm) && vm.IsExternalConflictOpen);
            var card = dialog.Content as Border;
            Assert.NotNull(card);

            var escape = card!.KeyBindings.SingleOrDefault(b => b.Gesture == new KeyGesture(Key.Escape));
            Assert.NotNull(escape);

            escape!.Command!.Execute(escape.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            // Decidir luego cierra el diálogo pero no reanuda el autoguardado; el aviso de la cabecera es
            // lo que impide que esa pausa quede invisible.
            Assert.False(vm.IsExternalConflictOpen);
            Assert.True(vm.IsAutosavePaused);
            Assert.True(vm.ShowExternalConflictBadge);
        }
        finally
        {
            window?.Close();
            EditorPreferences.IsZenMode = previousZen;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    /// <summary>El aviso de la cabecera se dibuja incluso en la vista simplificada, donde el resto de
    /// adornos desaparecen: no es un adorno, es la única señal de que el autoguardado está en pausa.</summary>
    [Fact]
    public void ConflictBadge_IsRendered_WhileTheDecisionIsPending()
    {
        HeadlessApp.EnsureInitialized();
        var directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
        Window? window = null;
        var previousZen = EditorPreferences.IsZenMode;
        var previousSimplified = EditorPreferences.SimplifiedNotesView;
        try
        {
            EditorPreferences.IsZenMode = false;
            EditorPreferences.SimplifiedNotesView = true;
            var (view, vm, note, _) = CreateEditor(directory);
            window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();

            RaiseConflict(vm, note);
            vm.PostponeExternalDecisionCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var badge = view.GetVisualDescendants().OfType<Button>()
                .SingleOrDefault(b => ReferenceEquals(b.Command, vm.ReopenExternalConflictCommand));
            Assert.NotNull(badge);
            Assert.True(badge!.IsVisible);
        }
        finally
        {
            window?.Close();
            EditorPreferences.IsZenMode = previousZen;
            EditorPreferences.SimplifiedNotesView = previousSimplified;
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
