using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una fila del selector "Nueva nota desde plantilla". Envuelve el <see cref="NoteTemplate"/> leído
/// del disco sin copiarle nada más que lo que la lista pinta, igual que <see cref="NoteCardViewModel"/>
/// hace con <see cref="NoteFile"/>: la plantilla real sigue siendo el archivo.</summary>
public sealed partial class NoteTemplateItemViewModel : ObservableObject
{
    public NoteTemplate Template { get; }

    public string Name => Template.Name;

    /// <summary>La descripción de la plantilla o, si no declara ninguna, su ruta dentro de la carpeta de
    /// plantillas — que en una plantilla guardada en una subcarpeta es justo lo que la distingue de otra
    /// con el mismo nombre, y en una de la raíz sigue diciendo de qué archivo se trata.</summary>
    public string Description => string.IsNullOrWhiteSpace(Template.Description)
        ? Template.RelativePath
        : Template.Description;

    /// <summary>Marca la plantilla elegida en Ajustes como la de la nota diaria, para que el selector
    /// muestre en su fila la misma información que Ajustes y no haya que ir a comprobarla allí.</summary>
    public bool IsDailyTemplate { get; }

    public NoteTemplateItemViewModel(NoteTemplate template, bool isDailyTemplate)
    {
        Template = template;
        IsDailyTemplate = isDailyTemplate;
    }
}
