using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Contenedor de la sección IA Skills: monta la disposición elegida (explorador o árbol) y
/// mantiene los diálogos modales, que son los mismos en las dos. Solo mecánica de vista — todo lo que
/// toca la biblioteca vive en <see cref="SkillsViewModel"/>.</summary>
public partial class SkillsView : UserControl
{
    /// <summary>Qué disposición hay montada ahora mismo en <c>LayoutHost</c>, o null si todavía ninguna.
    /// Se compara contra <see cref="SkillsViewModel.IsTreeView"/> para no reconstruir la vista (y con ella
    /// su editor Markdown, que es caro) en cada notificación de cambio de propiedad.</summary>
    private bool? _hostedLayoutIsTree;

    public SkillsView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
    }

    private SkillsViewModel? Vm => DataContext as SkillsViewModel;

    private void ApplyLayout()
    {
        if (Vm is not { } vm) return;

        // La suscripción se rehace en cada DataContext nuevo; quitarla antes evita duplicarla si esta
        // misma instancia se reutiliza con otro viewmodel.
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (_hostedLayoutIsTree == vm.IsTreeView && LayoutHost.Content is not null) return;

        _hostedLayoutIsTree = vm.IsTreeView;
        LayoutHost.Content = vm.IsTreeView ? new SkillsTreeView() : (Control)new SkillsBrowserView();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkillsViewModel.IsTreeView)) ApplyLayout();
        if (e.PropertyName == nameof(SkillsViewModel.NamePrompt) && Vm?.NamePrompt is not null)
            Dispatcher.UIThread.Post(() =>
            {
                var input = this.FindControl<TextBox>("NamePromptTextBox");
                input?.Focus();
                input?.SelectAll();
            }, DispatcherPriority.Input);
    }

    private void NamePromptTextBox_Initialized(object? sender, EventArgs e)
    {
        if (sender is not TextBox textBox) return;
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    private void NamePromptTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Vm?.ConfirmNamePromptCommand.Execute(null);
                break;
            case Key.Escape:
                e.Handled = true;
                Vm?.CancelNamePromptCommand.Execute(null);
                break;
        }
    }
}
