using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Una carpeta dibujada como tarjeta en la rejilla del modo explorador de Archivos, incluida la entrada
/// ".." que sube a la carpeta padre — misma pieza que <see cref="SkillFolderCardViewModel"/> en IA Skills. Es un
/// tipo aparte de <see cref="FileFolderViewModel"/> (la fila del panel lateral) porque no tiene estado de árbol:
/// una tarjeta no se expande ni recuerda hijos. Sí se renombra en línea y es destino de arrastre, igual que la
/// fila, para que la carpeta se pueda organizar desde donde se está mirando.
///
/// También hace de carga del diálogo de confirmación de borrado de carpeta (<c>FilesViewModel.FolderPendingDelete</c>),
/// que solo necesita nombre y ruta venga de la fila, de la tarjeta o del árbol.</summary>
public sealed partial class FileFolderCardViewModel : ObservableObject
{
    public string Name { get; }
    public string FullPath { get; }

    /// <summary>La tarjeta de subir un nivel: otro icono, sin contador, sin menú contextual, y al abrirla se
    /// navega al padre en vez de entrar.</summary>
    public bool IsParent { get; }

    /// <summary>Subcarpetas más archivos que no son notas (ver <c>VaultService.CountFolderEntries</c>).</summary>
    public int EntryCount { get; }

    /// <summary>"3 elementos", ya pluralizado en el idioma activo, o "Subir un nivel" para la tarjeta "..".</summary>
    public string CountLabel => IsParent
        ? LocalizationService.Instance["Files_UpOneLevel"]
        : Plural.Get("Files_FolderEntries", EntryCount, EntryCount);

    [ObservableProperty]
    private bool _isDropTarget;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _editingName = string.Empty;

    public FileFolderCardViewModel(string name, string fullPath, bool isParent = false, int entryCount = 0)
    {
        Name = name;
        FullPath = fullPath;
        IsParent = isParent;
        EntryCount = entryCount;
    }

    [RelayCommand]
    private void BeginRename()
    {
        if (IsParent) return;
        EditingName = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;
}
