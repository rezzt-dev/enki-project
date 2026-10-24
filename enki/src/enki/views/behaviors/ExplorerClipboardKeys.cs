using System;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Atajos del explorador limitados a sus filas y tarjetas; el editor conserva los suyos.</summary>
internal static class ExplorerClipboardKeys
{
    public static void Attach(UserControl view)
    {
        view.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Source is not Visual source) return;
            var ancestors = source.GetVisualAncestors().Prepend(source).ToArray();
            if (ancestors.Any(v => v is TextBox or EditorView or FilePreviewView)) return;
            var target = ancestors.OfType<Control>().Select(c => c.DataContext).FirstOrDefault(c =>
                c is NoteCardViewModel or NoteTreeItemViewModel or NoteFolderViewModel or NoteFolderCardViewModel
                    or FileCardViewModel or FileTreeItemViewModel or FileFolderViewModel or FileFolderCardViewModel);
            if (target is null && view.DataContext is FilesViewModel { IsTreeView: true } files)
                target = files.SelectedTreeNode;
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            var action = e.KeyModifiers == modifier ? e.Key : e.Key == Key.Escape ? Key.Escape : Key.None;
            ICommand? command = view.DataContext switch
            {
                NotesViewModel vm => action switch
                {
                    Key.C => vm.CopyEntryCommand, Key.X => vm.CutEntryCommand,
                    Key.V => vm.PasteEntryCommand, Key.Escape => vm.CancelClipboardCommand, _ => null
                },
                FilesViewModel vm => action switch
                {
                    Key.C => vm.CopyEntryCommand, Key.X => vm.CutEntryCommand,
                    Key.V => vm.PasteEntryCommand, Key.Escape => vm.CancelClipboardCommand, _ => null
                },
                _ => null
            };
            if (command?.CanExecute(target) != true) return;
            command.Execute(target);
            e.Handled = true;
        }, RoutingStrategies.Bubble);
    }
}
