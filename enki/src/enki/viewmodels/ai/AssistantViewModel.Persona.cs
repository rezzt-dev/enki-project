using System;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Personalidad del asistente (<see cref="AiPersona"/>): el nombre con el que se presenta y se muestra
/// en la interfaz, y el contexto por capas que viaja en cada turno —general (Ajustes), de la sesión del chat y
/// del propio chat—. El menú de la cabecera edita las dos capas que dependen de dónde se está; la general se
/// edita en Ajustes › Inteligencia artificial. Ver <c>.docs/project-info/asistente-ia.md</c>, sección
/// «personalidad».</summary>
public sealed partial class AssistantViewModel
{
    /// <summary>Contexto escrito para el chat que aún no se ha empezado; pasa al chat al crearlo.</summary>
    private string? _draftChatContext;

    private AiPersonaService Persona => _chats.Persona;

    // ===================== Nombre =====================

    /// <summary>Nombre que el usuario le ha puesto, o el genérico de la interfaz.</summary>
    public string AssistantName => Persona.Name ?? LocalizationService.Instance["Assistant_Message_Author"];

    public bool HasCustomName => Persona.Name is not null;

    public string WelcomeTitle => Persona.Name is { } name
        ? PersonaFormat("Assistant_Welcome_TitleNamed", name)
        : LocalizationService.Instance["Assistant_Welcome_Title"];

    public string ComposerPlaceholder => Persona.Name is { } name
        ? PersonaFormat("Assistant_Composer_PlaceholderNamed", name)
        : LocalizationService.Instance["Assistant_Composer_Placeholder"];

    // ===================== Capas de contexto =====================

    /// <summary>Contexto del chat abierto o del que se va a empezar.</summary>
    private string ChatContext => (_activeSession is not null ? _activeSession.Context : _draftChatContext) ?? string.Empty;

    /// <summary>Contexto guardado para la sesión del chat; vacío en un chat del vault.</summary>
    private string SessionContext => ScopeSessionId is { } sessionId ? Persona.GetSessionContext(VaultKey, sessionId) : string.Empty;

    /// <summary>La sesión del chat existe en el vault abierto: sin ella no hay a qué asociar su contexto.</summary>
    public bool CanEditSessionContext => IsSessionChat && _scopeSession is not null && _vaultService.IsVaultConfigured;

    /// <summary>Un chat sin vault asociado se puede leer pero no continuar, así que tampoco se le cambia el contexto.</summary>
    public bool CanEditChatContext => !IsUnassignedChat;

    public bool HasPersonaContext =>
        Persona.Persona.Context.Length > 0 || SessionContext.Length > 0 || ChatContext.Length > 0;

    /// <summary>Qué capas están activas en este chat, para el botón de la cabecera y el menú.</summary>
    public string PersonaLayersSummary
    {
        get
        {
            var layers = new System.Collections.Generic.List<string>(3);
            if (Persona.Persona.Context.Length > 0) layers.Add(LocalizationService.Instance["Assistant_Persona_Layer_General"]);
            if (SessionContext.Length > 0) layers.Add(LocalizationService.Instance["Assistant_Persona_Layer_Session"]);
            if (ChatContext.Length > 0) layers.Add(LocalizationService.Instance["Assistant_Persona_Layer_Chat"]);
            return layers.Count == 0
                ? LocalizationService.Instance["Assistant_Persona_NoLayers"]
                : string.Join(" · ", layers);
        }
    }

    public string PersonaTooltip => PersonaFormat("Assistant_Persona_Tooltip", AssistantName, PersonaLayersSummary);

    /// <summary>Principio del contexto general, para que el menú enseñe qué hay debajo sin repetirlo entero.</summary>
    public string PersonaGeneralPreview
    {
        get
        {
            var context = Persona.Persona.Context;
            if (context.Length == 0) return LocalizationService.Instance["Assistant_Persona_General_Empty"];
            var singleLine = string.Join(' ', context.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return singleLine.Length <= 160 ? singleLine : singleLine[..159].TrimEnd() + "…";
        }
    }

    public string PersonaSessionTitle => PersonaFormat("Assistant_Persona_Session_Title", ScopeLabel);

    // ===================== Menú de la cabecera =====================

    [ObservableProperty]
    private bool _isPersonaEditorOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersonaChanges), nameof(PersonaSessionCounter))]
    [NotifyCanExecuteChangedFor(nameof(SavePersonaCommand))]
    private string _personaSessionContextDraft = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersonaChanges), nameof(PersonaChatCounter))]
    [NotifyCanExecuteChangedFor(nameof(SavePersonaCommand))]
    private string _personaChatContextDraft = string.Empty;

    /// <summary>Por qué no se pudo guardar lo último; <see langword="null"/> si no hubo problema.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPersonaError))]
    private string? _personaError;

    public bool HasPersonaError => PersonaError is not null;

    public string PersonaSessionCounter => PersonaCounter(PersonaSessionContextDraft);

    public string PersonaChatCounter => PersonaCounter(PersonaChatContextDraft);

    public bool HasPersonaChanges =>
        CanEditSessionContext && CleanPersonaText(PersonaSessionContextDraft) != SessionContext
        || CanEditChatContext && CleanPersonaText(PersonaChatContextDraft) != ChatContext;

    [RelayCommand]
    private void TogglePersonaEditor()
    {
        if (!IsPersonaEditorOpen) LoadPersonaDrafts();
        IsPersonaEditorOpen = !IsPersonaEditorOpen;
    }

    [RelayCommand]
    private void DiscardPersonaChanges()
    {
        LoadPersonaDrafts();
        IsPersonaEditorOpen = false;
    }

    /// <summary>Guarda el contexto de la sesión y el del chat. Se aplica desde el turno siguiente, también con una
    /// respuesta en curso: la que ya está llegando sigue con lo que tenía.</summary>
    [RelayCommand(CanExecute = nameof(HasPersonaChanges))]
    private void SavePersona()
    {
        PersonaError = null;
        var sessionContext = CleanPersonaText(PersonaSessionContextDraft);
        if (CanEditSessionContext && sessionContext != SessionContext)
        {
            try
            {
                EnsureVaultIdentity();
                Persona.SetSessionContext(VaultKey, ScopeSessionId!, sessionContext);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                LogService.Warning("No se pudo guardar el contexto de la sesión del asistente", ex);
                PersonaError = LocalizationService.Instance["Assistant_Persona_SaveFailed"];
                return;
            }
        }

        var chatContext = CleanPersonaText(PersonaChatContextDraft);
        if (CanEditChatContext && chatContext != ChatContext)
        {
            var value = chatContext.Length == 0 ? null : chatContext;
            if (_activeSession is { } session)
            {
                var previous = session.Context;
                session.Context = value;
                if (!TrySaveSession(session, touch: false))
                {
                    session.Context = previous;
                    PersonaError = LocalizationService.Instance["Assistant_Persona_SaveFailed"];
                    return;
                }
            }
            else
            {
                _draftChatContext = value;
                SaveDraft();
            }
        }

        RefreshPersona();
        IsPersonaEditorOpen = false;
    }

    [RelayCommand]
    private void OpenPersonaSettings()
    {
        IsPersonaEditorOpen = false;
        _openSettings();
    }

    private void LoadPersonaDrafts()
    {
        PersonaError = null;
        PersonaSessionContextDraft = SessionContext;
        PersonaChatContextDraft = ChatContext;
    }

    /// <summary>Lo que el modelo debe saber de la personalidad en este turno. Se lee en el momento de enviar,
    /// así que un cambio hecho a mitad de conversación se aplica en el turno siguiente.</summary>
    private AiPersonaNotice BuildPersonaNotice(AiChatSession session)
    {
        var persona = Persona.Persona;
        var sessionContext = session.SessionId is { } sessionId ? Persona.GetSessionContext(VaultKey, sessionId) : null;
        return new AiPersonaNotice(persona.Name, persona.Context,
            session.SessionId is null ? null : ScopeLabel, sessionContext, session.Context);
    }

    private void RefreshPersona()
    {
        OnPropertyChanged(nameof(AssistantName));
        OnPropertyChanged(nameof(HasCustomName));
        OnPropertyChanged(nameof(WelcomeTitle));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanEditSessionContext));
        OnPropertyChanged(nameof(CanEditChatContext));
        OnPropertyChanged(nameof(HasPersonaContext));
        OnPropertyChanged(nameof(PersonaLayersSummary));
        OnPropertyChanged(nameof(PersonaTooltip));
        OnPropertyChanged(nameof(PersonaGeneralPreview));
        OnPropertyChanged(nameof(PersonaSessionTitle));
        OnPropertyChanged(nameof(HasPersonaChanges));
        SavePersonaCommand.NotifyCanExecuteChanged();
        // Con el menú cerrado los borradores siguen al chat abierto; abierto, se respeta lo que se está escribiendo.
        if (!IsPersonaEditorOpen) LoadPersonaDrafts();
    }

    private static string CleanPersonaText(string? text) => AiPersona.CleanText(text, AiPersona.MaxScopedContextLength);

    private static string PersonaCounter(string? text) =>
        string.Create(CultureInfo.InvariantCulture, $"{(text ?? string.Empty).Length}/{AiPersona.MaxScopedContextLength}");

    private static string PersonaFormat(string key, params object[] args) =>
        string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance[key], args);
}
