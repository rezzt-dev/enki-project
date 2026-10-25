using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Las dos superficies del asistente y su indicador global. La sección Asistente es una página más; el
/// panel lateral es el mismo <see cref="AssistantViewModel"/> anclado a la derecha de cualquier otra sección,
/// para conversar sin salir de lo que se está haciendo: abrirlo no navega ni cambia la nota seleccionada.
/// Nunca están las dos a la vez —con la sección delante, el panel se oculta—, así que no hay dos compositores
/// editando el mismo borrador. Cerrar el panel solo lo oculta: la respuesta en curso sigue, y el botón de la
/// barra de título dice en qué estado está. Ver <c>.docs/project-info/asistente-ia.md</c>.</summary>
public partial class MainWindowViewModel
{
    /// <summary>El usuario abrió el panel lateral del asistente. Sigue abierto al navegar entre secciones.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAssistantPanelVisible))]
    private bool _isAssistantPanelOpen;

    /// <summary>Sección desde la que se pasó a la sección Asistente, para volver a ella al pedir el panel.</summary>
    private AppPage _pageBeforeAssistant = AppPage.Dashboard;

    /// <summary>El panel se enseña: abierto, con la IA encendida, sin la sección Asistente delante y fuera del
    /// modo enfoque, que deja la nota sola en pantalla igual que oculta la barra lateral.</summary>
    public bool IsAssistantPanelVisible => IsAssistantPanelOpen && AssistantPanelEnabled && !IsZenMode
        && CurrentPage is not AssistantViewModel;

    /// <summary>Si el usuario ve ahora la conversación, en la sección o en el panel. Decide si una decisión
    /// pendiente se avisa también con una notificación y si una respuesta queda como "lista sin ver".</summary>
    private bool IsAssistantVisible => CurrentPage is AssistantViewModel || IsAssistantPanelVisible;

    public double AssistantPanelWidth
    {
        get => _settingsService.Settings.Ai.PanelWidth;
        set
        {
            var clamped = Math.Clamp(value, AiSettings.MinPanelWidth, AiSettings.MaxPanelWidth);
            if (Math.Abs(clamped - _settingsService.Settings.Ai.PanelWidth) < 0.5) return;
            _settingsService.Settings.Ai.PanelWidth = clamped;
            OnPropertyChanged();
        }
    }

    /// <summary>El ancho se guarda al soltar el separador, no en cada píxel del arrastre.</summary>
    public void PersistAssistantPanelWidth()
    {
        try
        {
            _settingsService.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo guardar el ancho del panel del asistente", ex);
        }
    }

    partial void OnIsAssistantPanelOpenChanged(bool value)
    {
        if (value) Assistant?.OnActivated();
        else Assistant?.SaveDraft();
    }

    partial void OnCurrentPageChanged(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        if (oldValue is AssistantViewModel && newValue is not AssistantViewModel)
        {
            Assistant?.SaveDraft();
            // Al salir de la sección con el panel abierto, la conversación sigue a la vista en él.
            if (IsAssistantPanelOpen) Assistant?.OnActivated();
        }
        if (newValue is AssistantViewModel && oldValue is not null && oldValue is not AssistantViewModel)
            _pageBeforeAssistant = PageOf(oldValue) ?? _pageBeforeAssistant;
        OnPropertyChanged(nameof(IsAssistantPanelVisible));
    }

    partial void OnAssistantPanelEnabledChanged(bool value)
    {
        if (!value) IsAssistantPanelOpen = false;
        OnPropertyChanged(nameof(IsAssistantPanelVisible));
    }

    /// <summary>Botón del asistente de la barra de título y su atajo. Desde la sección Asistente pasa la
    /// conversación al panel y vuelve a la sección anterior; desde cualquier otra, abre o cierra el panel.</summary>
    [RelayCommand]
    private void ToggleAssistantPanel()
    {
        if (!AssistantPanelEnabled || Assistant is null) return;
        if (CurrentPage is AssistantViewModel)
        {
            ShowAssistantAsPanel();
            return;
        }
        IsAssistantPanelOpen = !IsAssistantPanelOpen;
    }

    /// <summary>"Ampliar": la conversación del panel pasa a la sección Asistente, con todo su estado.</summary>
    [RelayCommand]
    private void ExpandAssistantPanel()
    {
        if (!AssistantPanelEnabled) return;
        IsAssistantPanelOpen = false;
        Navigate(AppPage.Assistant);
    }

    /// <summary>"Abrir como panel" desde la sección: vuelve a la sección de antes con el chat al lado.</summary>
    [RelayCommand]
    private void ShowAssistantAsPanel()
    {
        if (!AssistantPanelEnabled) return;
        IsAssistantPanelOpen = true;
        Navigate(_pageBeforeAssistant == AppPage.Assistant ? AppPage.Dashboard : _pageBeforeAssistant);
    }

    [RelayCommand]
    private void CloseAssistantPanel() => IsAssistantPanelOpen = false;

    /// <summary>Lleva la vista a la conversación para los accesos de otras secciones (skills, sesiones, paleta):
    /// en el panel si el usuario lo tiene abierto —sin moverlo de donde está—, si no en la sección.</summary>
    private void ShowAssistant()
    {
        if (IsAssistantPanelVisible) Assistant?.OnActivated();
        else Navigate(AppPage.Assistant);
    }

    private AppPage? PageOf(ViewModelBase page) => page switch
    {
        _ when ReferenceEquals(page, Dashboard) => AppPage.Dashboard,
        _ when ReferenceEquals(page, Calendar) => AppPage.Calendar,
        _ when ReferenceEquals(page, Notes) => AppPage.Notes,
        _ when ReferenceEquals(page, Files) => AppPage.Files,
        _ when ReferenceEquals(page, Skills) => AppPage.Skills,
        _ when ReferenceEquals(page, Sessions) => AppPage.Sessions,
        _ when ReferenceEquals(page, Kanban) => AppPage.Kanban,
        _ when ReferenceEquals(page, Trash) => AppPage.Trash,
        _ when ReferenceEquals(page, TagManager) => AppPage.TagManager,
        _ when ReferenceEquals(page, Habits) => AppPage.Habits,
        _ when ReferenceEquals(page, Statistics) => AppPage.Statistics,
        _ when ReferenceEquals(page, Reminders) => AppPage.Reminders,
        _ when ReferenceEquals(page, Notifications) => AppPage.Notifications,
        _ => null
    };
}
