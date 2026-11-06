using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una entrada del desplegable "Plantilla de la nota diaria" (Ajustes › Editor › Plantillas).
/// Existe como viewmodel propio en vez de como una lista de cadenas —el patrón de los desplegables de
/// fuentes de esta misma sección— porque lo que hay que guardar no es lo que se lee: dos plantillas de
/// subcarpetas distintas pueden llamarse igual, y el ajuste almacena la ruta relativa, no el nombre.</summary>
public sealed class NoteTemplateOptionViewModel
{
    /// <summary>Ruta relativa dentro de la carpeta de plantillas, o null para la opción "sin
    /// plantilla".</summary>
    public string? RelativePath { get; }

    public string Label { get; }

    private NoteTemplateOptionViewModel(string? relativePath, string label)
    {
        RelativePath = relativePath;
        Label = label;
    }

    /// <summary>La opción "sin plantilla": la nota diaria se comporta como antes de existir esta
    /// funcionalidad.</summary>
    public static NoteTemplateOptionViewModel None() =>
        new(null, LocalizationService.Instance["Settings_Editor_Templates_None"]);

    /// <summary>Etiqueta una plantilla con su nombre y, entre paréntesis, su ruta relativa cuando vive en
    /// una subcarpeta — así dos "Diario" en carpetas distintas se distinguen en la lista.</summary>
    public static NoteTemplateOptionViewModel For(NoteTemplate template) =>
        new(template.RelativePath,
            template.RelativePath.Contains('/')
                ? $"{template.Name} ({template.RelativePath})"
                : template.Name);
}
