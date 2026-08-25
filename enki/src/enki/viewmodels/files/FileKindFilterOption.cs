using enki.Models;

namespace enki.ViewModels;

/// <summary>Una opción del desplegable de filtro de Archivos — \"Todos los archivos\" cuando
/// <see cref="Kind"/> es null, o un <see cref="VaultFileKind"/> concreto.</summary>
public sealed class FileKindFilterOption
{
    /// <summary>Null = \"Todos los archivos\" (sin filtro).</summary>
    public VaultFileKind? Kind { get; }

    /// <summary>Etiqueta localizada que se muestra en el ComboBox.</summary>
    public string Label { get; }

    public FileKindFilterOption(VaultFileKind? kind, string label)
    {
        Kind = kind;
        Label = label;
    }

    public override string ToString() => Label;
}