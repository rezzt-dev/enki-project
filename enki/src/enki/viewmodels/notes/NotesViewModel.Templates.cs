using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Documentos › "Desde plantilla": el selector de plantillas y las acciones que lo acompañan
/// (crear una nota a partir de una, crear o editar la propia plantilla, abrir su carpeta). Partial aparte
/// de <c>NotesViewModel.cs</c> por el mismo criterio que <c>NotesViewModel.SimpleView.cs</c> — es un flujo
/// completo con su propio estado de interfaz, y mezclarlo con la rejilla/pestañas solo haría más largo un
/// archivo que ya lo es.
///
/// <para>El catálogo se relee del disco cada vez que se abre el selector en vez de mantenerse cacheado:
/// las plantillas son archivos que el usuario puede haber tocado desde fuera de Enki (o desde una pestaña
/// del propio editor, que es justo lo que hace <see cref="EditTemplate"/>), y una lista cacheada sería una
/// lista desactualizada en cuanto eso pasara. Son unos pocos ficheros pequeños y solo se leen al abrir el
/// selector, así que no hay nada que optimizar aquí.</para></summary>
public sealed partial class NotesViewModel
{
    public ObservableCollection<NoteTemplateItemViewModel> Templates { get; } = [];

    [ObservableProperty]
    private bool _isTemplatePickerOpen;

    /// <summary>Plantilla que espera confirmación de borrado, con el mismo patrón —y el mismo ajuste
    /// "Preguntar antes de borrar"— que <see cref="NotePendingDelete"/>. Null cuando no hay ninguna.</summary>
    [ObservableProperty]
    private NoteTemplateItemViewModel? _templatePendingDelete;

    /// <summary>Si la carpeta de plantillas tiene algo que ofrecer. El selector alterna entre la lista y un
    /// estado vacío que explica qué es una plantilla y ofrece crear la primera.</summary>
    public bool HasTemplates => Templates.Count > 0;

    /// <summary>Permite traer los ejemplos nuevos a un vault anterior sin resembrar los que el usuario
    /// eliminó o editó. La acción desaparece cuando ambos archivos ya existen.</summary>
    public bool HasMissingSuggestedTemplates =>
        !File.Exists(Path.Combine(_templateService.TemplatesDirectory, "project.md")) ||
        !File.Exists(Path.Combine(_templateService.TemplatesDirectory, "reading.md"));

    /// <summary>Dónde nace una nota creada desde plantilla: la carpeta que se está mirando en la rejilla
    /// (vista completa) o la raíz del espacio de trabajo activo (vista simplificada, que no tiene concepto
    /// de "carpeta actual"). Exactamente el mismo destino que elegiría el botón "Nueva nota" de cada una de
    /// las dos vistas, para que cambiar de vista no cambie dónde acaban las notas.</summary>
    private string TemplateTargetFolder => IsSimplifiedView ? EffectiveRootPath : CurrentFolderPath;

    [RelayCommand]
    private void ShowTemplatePicker()
    {
        ReloadTemplates();
        TemplatePendingDelete = null;
        IsTemplatePickerOpen = true;
    }

    [RelayCommand]
    private void CloseTemplatePicker()
    {
        IsTemplatePickerOpen = false;
        TemplatePendingDelete = null;
    }

    /// <summary>Relee el catálogo de plantillas y marca cuál es la de la nota diaria. Internal —y no
    /// privado— porque los tests comprueban el catálogo sin pasar por el selector.</summary>
    internal void ReloadTemplates()
    {
        var dailyTemplatePath = _vaultSettingsService?.Settings.DailyNoteTemplate;

        Templates.Clear();
        foreach (var template in _templateService.LoadTemplates())
        {
            var isDaily = !string.IsNullOrWhiteSpace(dailyTemplatePath) &&
                string.Equals(template.RelativePath, dailyTemplatePath, VaultService.PathComparison);
            Templates.Add(new NoteTemplateItemViewModel(template, isDaily));
        }

        OnPropertyChanged(nameof(HasTemplates));
        OnPropertyChanged(nameof(HasMissingSuggestedTemplates));
    }

    /// <summary>Crea la nota a partir de la plantilla elegida y la abre, con el cursor ya donde la
    /// plantilla pedía (<c>{{cursor}}</c>) si lo pedía en alguna parte.</summary>
    [RelayCommand]
    private void CreateNoteFromTemplate(NoteTemplateItemViewModel item)
    {
        // Se relee del disco en lugar de usar el cuerpo que se cargó al abrir el selector: entre una cosa y
        // otra el usuario ha podido guardar cambios en la pestaña de esa misma plantilla, y la nota debe
        // salir de lo que hay ahora en el archivo.
        NoteTemplate template;
        try
        {
            template = _templateService.Read(item.Template.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        var draft = NoteTemplateService.Prepare(template, template.Name);

        NoteFile note;
        try
        {
            note = _vaultService.CreateNote(draft.Title, TemplateTargetFolder, draft.Content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        CloseTemplatePicker();
        Reload();
        OpenNote(note, draft.CaretOffset);
    }

    /// <summary>Crea una plantilla nueva ya rellena con un ejemplo de la sintaxis y la abre como una
    /// pestaña más del editor: una plantilla es un <c>.md</c> del vault, así que se edita con el mismo
    /// editor que cualquier nota en vez de con un formulario propio que habría que mantener en paralelo.</summary>
    [RelayCommand]
    private void CreateTemplate()
    {
        NoteTemplate template;
        try
        {
            template = _templateService.CreateTemplate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        CloseTemplatePicker();
        OpenTemplateFile(template);
    }

    [RelayCommand]
    private void AddSuggestedTemplates()
    {
        try { _templateService.AddSuggestedTemplates(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }

        ReloadTemplates();
    }

    [RelayCommand]
    private void EditTemplate(NoteTemplateItemViewModel item)
    {
        CloseTemplatePicker();
        OpenTemplateFile(item.Template);
    }

    /// <summary>Abre el archivo de plantilla en una pestaña del editor. Si el archivo ya no está (borrado
    /// desde fuera mientras el selector estaba abierto), se refresca el catálogo en vez de abrir una
    /// pestaña sobre la nada.</summary>
    private void OpenTemplateFile(NoteTemplate template)
    {
        var note = _vaultService.GetNote(template.FilePath);
        if (note is null)
        {
            ReloadTemplates();
            return;
        }

        OpenNote(note);
    }

    /// <summary>Pide borrar una plantilla. Respeta "Preguntar antes de borrar" (Ajustes › General) igual
    /// que el resto de borrados de la app; con la confirmación desactivada, borra directamente.</summary>
    [RelayCommand]
    private void RequestDeleteTemplate(NoteTemplateItemViewModel item)
    {
        if (_settingsService?.Settings.ConfirmBeforeDelete ?? true) TemplatePendingDelete = item;
        else DeleteTemplate(item);
    }

    [RelayCommand]
    private void CancelDeleteTemplate() => TemplatePendingDelete = null;

    [RelayCommand]
    private void ConfirmDeleteTemplate()
    {
        if (TemplatePendingDelete is not { } item) return;
        DeleteTemplate(item);
    }

    private void DeleteTemplate(NoteTemplateItemViewModel item)
    {
        try
        {
            _templateService.DeleteTemplate(item.Template);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NotifyVaultWriteFailed(ex);
            return;
        }
        finally
        {
            TemplatePendingDelete = null;
        }

        // La plantilla borrada podía ser la de la nota diaria: el ajuste se queda apuntando a una ruta que
        // ya no existe, que es justo el caso que NoteTemplateService.FindTemplate trata como "sin
        // plantilla". No se limpia aquí para que restaurar el archivo (o recrearlo con el mismo nombre)
        // devuelva la configuración a donde estaba.
        ReloadTemplates();
    }

    /// <summary>Abre la carpeta de plantillas en el explorador del sistema — la vía para organizarlas en
    /// subcarpetas, versionarlas o traerlas de otro vault, que es trabajo de gestor de archivos y no algo
    /// que tenga sentido reimplementar dentro de un selector.</summary>
    [RelayCommand]
    private void OpenTemplatesFolder()
    {
        _templateService.EnsureInitialized();
        if (Directory.Exists(_templateService.TemplatesDirectory))
            FolderOpener.Open(_templateService.TemplatesDirectory);
    }
}
