using System;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Inteligencia artificial › Personalidad: el nombre del asistente y el contexto general que
/// le acompaña en todos los chats (<see cref="AiPersona"/>). Se edita como borrador y se guarda con un botón,
/// para no reescribir el archivo en cada pulsación; el asistente lo aplica desde el turno siguiente. Los
/// contextos de sesión y de chat se editan desde la cabecera del asistente.</summary>
public sealed partial class SettingsViewModel
{
    private AiPersonaService? AiPersonaStore => _aiChatService?.Persona;

    /// <summary>Sin el servicio del asistente (algunos tests) la tarjeta no se puede usar.</summary>
    public bool CanEditAiPersona => AiPersonaStore is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiPersonaChanges), nameof(AiPersonaNameCounter))]
    [NotifyCanExecuteChangedFor(nameof(SaveAiPersonaCommand), nameof(DiscardAiPersonaChangesCommand))]
    private string _aiPersonaNameDraft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiPersonaChanges), nameof(AiPersonaContextCounter))]
    [NotifyCanExecuteChangedFor(nameof(SaveAiPersonaCommand), nameof(DiscardAiPersonaChangesCommand))]
    private string _aiPersonaContextDraft = string.Empty;

    /// <summary>Resultado del último guardado: confirmación o motivo del fallo. Se borra al volver a editar.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAiPersonaStatus))]
    private string? _aiPersonaStatus;

    [ObservableProperty]
    private bool _aiPersonaStatusIsError;

    public bool HasAiPersonaStatus => AiPersonaStatus is not null;

    public string AiPersonaNameCounter => AiPersonaCounter(AiPersonaNameDraft, AiPersona.MaxNameLength);

    public string AiPersonaContextCounter => AiPersonaCounter(AiPersonaContextDraft, AiPersona.MaxContextLength);

    public bool HasAiPersonaChanges => AiPersonaStore is { } service
        && (AiPersona.CleanName(AiPersonaNameDraft) != service.Persona.Name
            || AiPersona.CleanText(AiPersonaContextDraft, AiPersona.MaxContextLength) != service.Persona.Context);

    /// <summary>Hay cambios sin guardar que se pueden descartar.</summary>
    private bool CanDiscardAiPersona => HasAiPersonaChanges;

    private bool _loadingAiPersona;

    /// <summary>Lo último que se cargó en los campos. Con él se sabe si hay algo a medias cuando la personalidad
    /// cambia desde otro sitio: compararlo con lo guardado no sirve, porque lo guardado ya es lo nuevo.</summary>
    private (string Name, string Context) _aiPersonaLoaded = (string.Empty, string.Empty);

    private bool HasUnsavedAiPersonaEdits =>
        AiPersona.CleanName(AiPersonaNameDraft) != _aiPersonaLoaded.Name
        || AiPersona.CleanText(AiPersonaContextDraft, AiPersona.MaxContextLength) != _aiPersonaLoaded.Context;

    private void InitializeAiPersona()
    {
        if (AiPersonaStore is not { } service) return;
        service.PersonaChanged += () =>
        {
            // Otro sitio (o este mismo guardado) cambió la personalidad: se recarga salvo que haya algo a medias.
            if (!HasUnsavedAiPersonaEdits) LoadAiPersona();
            OnPropertyChanged(nameof(HasAiPersonaChanges));
            SaveAiPersonaCommand.NotifyCanExecuteChanged();
            DiscardAiPersonaChangesCommand.NotifyCanExecuteChanged();
        };
        LoadAiPersona();
    }

    private void LoadAiPersona()
    {
        if (AiPersonaStore is not { } service) return;
        _loadingAiPersona = true;
        try
        {
            _aiPersonaLoaded = (service.Persona.Name, service.Persona.Context);
            AiPersonaNameDraft = service.Persona.Name;
            AiPersonaContextDraft = service.Persona.Context;
        }
        finally
        {
            _loadingAiPersona = false;
        }
    }

    partial void OnAiPersonaNameDraftChanged(string value)
    {
        if (!_loadingAiPersona) AiPersonaStatus = null;
    }

    partial void OnAiPersonaContextDraftChanged(string value)
    {
        if (!_loadingAiPersona) AiPersonaStatus = null;
    }

    [RelayCommand(CanExecute = nameof(HasAiPersonaChanges))]
    private void SaveAiPersona()
    {
        if (AiPersonaStore is not { } service) return;
        try
        {
            service.SetIdentity(AiPersonaNameDraft, AiPersonaContextDraft);
            LoadAiPersona();
            AiPersonaStatusIsError = false;
            AiPersonaStatus = LocalizationService.Instance["Settings_Ai_Persona_Saved"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            LogService.Warning("No se pudo guardar la personalidad del asistente", ex);
            AiPersonaStatusIsError = true;
            AiPersonaStatus = LocalizationService.Instance["Settings_Ai_Persona_SaveFailed"];
        }
        SaveAiPersonaCommand.NotifyCanExecuteChanged();
        DiscardAiPersonaChangesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasAiPersonaChanges));
    }

    [RelayCommand(CanExecute = nameof(CanDiscardAiPersona))]
    private void DiscardAiPersonaChanges()
    {
        LoadAiPersona();
        AiPersonaStatus = null;
    }

    private static string AiPersonaCounter(string? text, int max) =>
        string.Create(CultureInfo.InvariantCulture, $"{(text ?? string.Empty).Length}/{max}");
}
