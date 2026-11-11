using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Estado del proveedor tal como lo ven Ajustes y el Asistente. Una sola instancia compartida
/// (la compone <see cref="MainWindowViewModel"/>), así que "Comprobar de nuevo" en un sitio actualiza el otro
/// y el indicador de comprobación en curso es el mismo en los dos.</summary>
public sealed partial class ClaudeCliStatusViewModel : ObservableObject
{
    private readonly ClaudeCliService _cli;
    private readonly Action<Action> _dispatch;

    public ClaudeCliStatusViewModel(ClaudeCliService cli, Action<Action>? dispatch = null, AiBackendService? backend = null)
    {
        _cli = cli;
        Backend = backend;
        _status = backend?.Status ?? cli.Status;
        _dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));
        if (backend is not null)
            backend.BusyChanged += () => _dispatch(() =>
            {
                OnPropertyChanged(nameof(IsBusy));
                RefreshCommand.NotifyCanExecuteChanged();
            });
        if (backend is null) cli.StatusChanged += status => _dispatch(() => Status = status);
        else backend.StatusChanged += status => _dispatch(() =>
        {
            Status = status;
            RefreshLocalizedLabels();
        });
    }

    public bool IsBusy => Backend?.IsBusy == true;

    /// <summary>Almacén de la clave de API del proveedor activo (la de Anthropic para Claude, la de Google AI
    /// Studio para Gemini), compartido con Ajustes. <see langword="null"/> para los que no usan clave.</summary>
    public AiApiKeyStore? ApiKeys => ApiKeysFor(Provider);

    public AiApiKeyStore? ApiKeysFor(AiProvider provider) => provider switch
    {
        AiProvider.Gemini => Backend?.GeminiApiKeys,
        AiProvider.Claude => _cli.ApiKeys,
        _ => null
    };
    public AiBackendService? Backend { get; }
    public AiProvider Provider => Backend?.Provider ?? AiProvider.Claude;
    public string ProviderLabel => AiProviderOption.LabelFor(Provider);
    public string SetupDocumentationUrl => Provider switch
    {
        AiProvider.OpenAi => CodexCliService.SetupDocumentationUrl,
        AiProvider.Ollama => "https://docs.ollama.com/quickstart",
        AiProvider.Gemini => GeminiApiService.DocumentationUrl,
        _ => ClaudeCliService.SetupDocumentationUrl
    };
    public string TermsUrl => Provider switch
    {
        AiProvider.Claude => ClaudeCliService.LegalAndComplianceUrl,
        AiProvider.Ollama => "https://ollama.com/terms",
        AiProvider.Gemini => GeminiApiService.TermsUrl,
        _ => "https://openai.com/policies/terms-of-use/"
    };

    /// <summary>El proveedor se pone en marcha con una orden de terminal (instalar, iniciar sesión, arrancar el
    /// servidor). Gemini no: solo necesita la clave, que se guarda en Ajustes.</summary>
    public bool HasSetupCommand => Provider != AiProvider.Gemini;

    /// <summary>Título del panel de puesta en marcha cuando falta la clave de API del proveedor activo.</summary>
    public string ApiKeySetupTitle => LocalizationService.Instance[Provider == AiProvider.Gemini ? "Ai_Gemini_Setup_Title" : "Assistant_Setup_ApiKey_Title"];

    /// <summary>Por qué falta la clave, o por qué la guardada no sirve (Gemini la comprueba al conectar).</summary>
    public string ApiKeySetupMessage => Provider == AiProvider.Gemini
        ? Status.Detail ?? LocalizationService.Instance["Ai_Gemini_Setup_Message"]
        : LocalizationService.Instance["Assistant_Setup_ApiKey_Message"];

    /// <summary>Gemini no se instala: su guía explica cómo conseguir la clave.</summary>
    public string SetupGuideLabel => LocalizationService.Instance[Provider == AiProvider.Gemini ? "Ai_Gemini_Guide" : "Assistant_Setup_OpenGuide"];

    /// <summary>Texto del enlace a la página del proveedor donde se crean las claves.</summary>
    public string ApiKeysPageLabel => LocalizationService.Instance[Provider == AiProvider.Gemini ? "Ai_Gemini_GetKey" : "Settings_Ai_ApiKey_Console"];

    [RelayCommand]
    private void OpenApiKeysPage() => UrlOpener.Open(ApiKeys?.KeysUrl ?? AiApiKeyStore.AnthropicKeysUrl);

    /// <summary>Momento de la última comprobación terminada, para no repetirla en cada navegación.</summary>
    public DateTime LastCheckedUtc { get; private set; } = DateTime.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsNotInstalled), nameof(IsSignedOut), nameof(IsUnsupported),
        nameof(IsKnown), nameof(ShowChecking), nameof(StateLabel), nameof(AccountLabel), nameof(HasAccount),
        nameof(VersionLabel), nameof(ExecutablePathLabel), nameof(UnsupportedMessage), nameof(SupportsVaultTools),
        nameof(SupportsVision), nameof(NeedsApiKey), nameof(NeedsLogin), nameof(ApiKeySetupMessage))]
    private ClaudeCliStatus _status;

    /// <summary>El CLI deja trabajar al asistente dentro del vault (ver
    /// <see cref="ClaudeCliStatus.SupportsVaultTools"/>); si no, el asistente solo responde con texto.</summary>
    public bool SupportsVaultTools => Status.SupportsVaultTools;

    /// <summary>El modelo elegido puede ver imágenes (ver <see cref="ClaudeCliStatus.SupportsVision"/>).</summary>
    public bool SupportsVision => Status.SupportsVision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChecking))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isChecking;

    public bool IsReady => Status.State == ClaudeCliState.Ready;
    public bool IsNotInstalled => Status.State == ClaudeCliState.NotInstalled;
    public bool IsSignedOut => Status.State == ClaudeCliState.SignedOut;
    public bool IsUnsupported => Status.State == ClaudeCliState.Unsupported;
    public bool IsKnown => Status.State != ClaudeCliState.Unknown;

    /// <summary>Falta la clave de API de Claude (modo de clave propia): se arregla en Ajustes, no en una terminal.</summary>
    public bool NeedsApiKey => IsSignedOut && Status.NeedsApiKey;

    /// <summary>Falta iniciar sesión en el CLI: se enseña la orden para una terminal.</summary>
    public bool NeedsLogin => IsSignedOut && !Status.NeedsApiKey;

    /// <summary>Primera comprobación aún en curso: todavía no se sabe qué enseñar.</summary>
    public bool ShowChecking => IsChecking && !IsKnown;

    public string StateLabel => Provider != AiProvider.Claude
        ? ProviderLabel + " · " + LocalizationService.Instance[IsReady ? "Ai_Ready" : IsNotInstalled ? "Ai_NotInstalled"
            : NeedsApiKey ? "Ai_ApiKeyMissing" : IsSignedOut ? "Ai_SignedOut" : IsUnsupported ? "Ai_Unavailable" : "Ai_Checking"]
        : LocalizationService.Instance[Status.State switch
    {
        ClaudeCliState.Ready => "Assistant_State_Ready",
        ClaudeCliState.NotInstalled => "Assistant_State_NotInstalled",
        ClaudeCliState.SignedOut when Status.NeedsApiKey => "Assistant_State_ApiKeyMissing",
        ClaudeCliState.SignedOut => "Assistant_State_SignedOut",
        ClaudeCliState.Unsupported => "Assistant_State_Unsupported",
        _ => "Assistant_State_Checking"
    }];

    /// <summary>"correo · Claude Pro", tal como lo informa <c>claude auth status</c>.</summary>
    public string AccountLabel
    {
        get
        {
            if (Provider == AiProvider.OpenAi)
                return IsReady ? Status.AuthMethod == "API" ? "OpenAI · API" : "OpenAI · ChatGPT" : string.Empty;
            if (Provider == AiProvider.Ollama) return IsReady ? "Ollama" : string.Empty;
            if (Provider == AiProvider.Gemini)
                return IsReady ? string.Join(" · ", new[] { "Google AI Studio", Status.AccountEmail }.Where(p => !string.IsNullOrEmpty(p))) : string.Empty;
            var plan = Status.SubscriptionType is { Length: > 0 } subscription
                ? "Claude " + char.ToUpperInvariant(subscription[0]) + subscription[1..]
                : Status.AuthMethod is { Length: > 0 } method && !method.Contains("claude", StringComparison.OrdinalIgnoreCase)
                    ? LocalizationService.Instance["Assistant_Status_ApiKey"]
                    : null;
            return string.Join(" · ", new[] { Status.AccountEmail, plan }.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    public bool HasAccount => AccountLabel.Length > 0;

    public string VersionLabel => Status.Version is { } version
        ? string.Format(LocalizationService.Instance.CurrentCulture, LocalizationService.Instance["Assistant_Status_Version"], version)
        : string.Empty;

    public string ExecutablePathLabel => Status.ExecutablePath ?? string.Empty;

    public string UnsupportedMessage => Provider != AiProvider.Claude
        ? Status.Detail ?? LocalizationService.Instance["Ai_UpdateCodex"]
        : string.Format(LocalizationService.Instance.CurrentCulture,
        LocalizationService.Instance["Assistant_Setup_Unsupported_Message"],
        Status.Version ?? "?", ClaudeCliService.MinimumVersion.ToString());

#pragma warning disable CA1822 // Enlazadas desde la vista: tienen que ser de instancia.
    public string InstallCommand => Provider == AiProvider.OpenAi ? "npm install -g @openai/codex"
        : Provider == AiProvider.Ollama ? "ollama serve" : OperatingSystem.IsWindows()
        ? ClaudeCliService.WindowsInstallCommand
        : ClaudeCliService.UnixInstallCommand;

    public string LoginCommand => Provider == AiProvider.OpenAi ? CodexCliService.LoginCommand
        : Provider == AiProvider.Ollama ? "ollama serve" : ClaudeCliService.LoginCommand;
#pragma warning restore CA1822

    private bool _refreshAgain;
    private bool CanRefresh => !IsChecking && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        if (IsChecking) { _refreshAgain = true; return; }
        IsChecking = true;
        try
        {
            // StatusChanged es la única vía de actualización: siempre en el hilo de interfaz. Asignar
            // también aquí puede adelantarse al evento y perder sus avisos si no hay SynchronizationContext.
            if (Backend is null) await _cli.DetectAsync();
            else await Backend.DetectAsync();
        }
        finally
        {
            _dispatch(() =>
            {
                IsChecking = false;
                LastCheckedUtc = DateTime.UtcNow;
                if (_refreshAgain)
                {
                    _refreshAgain = false;
                    _ = RefreshAsync();
                }
            });
        }
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Provider));
        OnPropertyChanged(nameof(ProviderLabel));
        OnPropertyChanged(nameof(InstallCommand));
        OnPropertyChanged(nameof(LoginCommand));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(AccountLabel));
        OnPropertyChanged(nameof(VersionLabel));
        OnPropertyChanged(nameof(UnsupportedMessage));
        OnPropertyChanged(nameof(ApiKeys));
        OnPropertyChanged(nameof(SetupDocumentationUrl));
        OnPropertyChanged(nameof(TermsUrl));
        OnPropertyChanged(nameof(HasSetupCommand));
        OnPropertyChanged(nameof(ApiKeySetupTitle));
        OnPropertyChanged(nameof(ApiKeySetupMessage));
        OnPropertyChanged(nameof(ApiKeysPageLabel));
        OnPropertyChanged(nameof(SetupGuideLabel));
    }
}
