using Avalonia.Controls;
using enki.ViewModels;

namespace enki.Views;

/// <summary>Contenedor de la sección Archivos: monta la disposición elegida (explorador o árbol) y
/// mantiene el diálogo de confirmación de borrado, que es el mismo en las dos. Solo mecánica de vista —
/// todo lo que toca el vault vive en <see cref="FilesViewModel"/>.</summary>
public partial class FilesView : UserControl
{
    /// <summary>Qué disposición hay montada ahora mismo en <c>LayoutHost</c>, o null si todavía ninguna.
    /// Se compara contra <see cref="FilesViewModel.IsTreeView"/> para no reconstruir la vista (y con ella
    /// su previsualización, que instala TextMate y abre PDFium) en cada notificación de cambio de
    /// propiedad.</summary>
    private bool? _hostedLayoutIsTree;

    public FilesView()
    {
        InitializeComponent();
        ExplorerClipboardKeys.Attach(this);

        DataContextChanged += (_, _) => ApplyLayout();
        Loaded += (_, _) => ApplyLayout();
    }

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    private void ApplyLayout()
    {
        if (Vm is not { } vm) return;

        // La suscripción se rehace en cada DataContext nuevo; quitarla antes evita duplicarla si esta
        // misma instancia se reutiliza con otro viewmodel.
        vm.PropertyChanged -= OnViewModelPropertyChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;

        if (_hostedLayoutIsTree == vm.IsTreeView && LayoutHost.Content is not null) return;

        _hostedLayoutIsTree = vm.IsTreeView;
        LayoutHost.Content = vm.IsTreeView ? new FilesTreeView() : (Control)new FilesBrowserView();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilesViewModel.IsTreeView)) ApplyLayout();
    }
}
