using enki.Localization;

namespace enki.ViewModels;

/// <summary>Una carpeta dibujada como tarjeta dentro de la rejilla del modo explorador, incluida la
/// entrada ".." que sube a la carpeta padre — misma pieza que <see cref="FileFolderCardViewModel"/> en
/// Archivos. Es un tipo aparte de <see cref="SkillFolderViewModel"/> (la fila del panel lateral) porque
/// no tiene estado de árbol: una tarjeta no se expande, no se renombra en línea y no recuerda hijos.</summary>
public sealed class SkillFolderCardViewModel
{
    public string Name { get; }
    public string FullPath { get; }

    /// <summary>La tarjeta de subir un nivel. Se pinta con otro icono y sin contador, y al abrirla
    /// <c>SkillsViewModel.OpenFolderCard</c> navega al padre en vez de entrar.</summary>
    public bool IsParent { get; }

    public int TotalSkillCount { get; }

    /// <summary>Etiqueta del contador ya pluralizada (§1.3/§1.4 de la guía de i18n) — la tarjeta no puede
    /// componerla en XAML con un literal fijo porque el plural depende del idioma activo.</summary>
    public string CountLabel => IsParent ? string.Empty : Plural.Get("Skills_Count", TotalSkillCount, TotalSkillCount);

    public SkillFolderCardViewModel(string name, string fullPath, bool isParent = false, int totalSkillCount = 0)
    {
        Name = name;
        FullPath = fullPath;
        IsParent = isParent;
        TotalSkillCount = totalSkillCount;
    }
}
