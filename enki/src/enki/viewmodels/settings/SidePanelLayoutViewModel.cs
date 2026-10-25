using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Preferencias compartidas por Ajustes y los paneles, sin duplicar el estado persistido.</summary>
public sealed class SidePanelLayoutViewModel(SettingsService settingsService, NotificationService notificationService)
    : ObservableObject
{
    public bool NotesOnRight
    {
        get => settingsService.Settings.NotesPanelOnRight;
        set => SetPosition(value, NotesOnRight, v => settingsService.Settings.NotesPanelOnRight = v, nameof(NotesOnRight));
    }

    public bool FilesOnRight
    {
        get => settingsService.Settings.FilesPanelOnRight;
        set => SetPosition(value, FilesOnRight, v => settingsService.Settings.FilesPanelOnRight = v, nameof(FilesOnRight));
    }

    public bool SkillsOnRight
    {
        get => settingsService.Settings.SkillsPanelOnRight;
        set => SetPosition(value, SkillsOnRight, v => settingsService.Settings.SkillsPanelOnRight = v, nameof(SkillsOnRight));
    }

    public bool AssistantOnRight
    {
        get => settingsService.Settings.AssistantPanelOnRight;
        set => SetPosition(value, AssistantOnRight, v => settingsService.Settings.AssistantPanelOnRight = v, nameof(AssistantOnRight));
    }

    private void SetPosition(bool value, bool previous, Action<bool> assign, string propertyName)
    {
        if (value == previous) return;
        assign(value);
        try
        {
            settingsService.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            assign(previous);
            LogService.Warning("No se pudo guardar la posición del panel lateral", ex);
            notificationService.Push(LocalizationService.Instance["Settings_SidePanels_ErrorTitle"],
                LocalizationService.Instance["Settings_SidePanels_Error"], NotificationSeverity.Error);
        }
        OnPropertyChanged(propertyName);
    }

    /// <summary>Actualiza los bindings después de cambiar de vault o importar preferencias.</summary>
    public void Reload()
    {
        OnPropertyChanged(nameof(NotesOnRight));
        OnPropertyChanged(nameof(FilesOnRight));
        OnPropertyChanged(nameof(SkillsOnRight));
        OnPropertyChanged(nameof(AssistantOnRight));
    }
}
