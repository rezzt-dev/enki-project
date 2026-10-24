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
    public AiBackendService? Backend { get; }
    public AiProvider Provider => Backend?.Provider ?? AiProvider.Claude;
    public string ProviderLabel => Provider switch { AiProvider.OpenAi => "OpenAI · ChatGPT", AiProvider.Ollama => "Ollama", _ => "Claude" };
    public string SetupDocumentationUrl => Provider switch
    {
        AiProvider.OpenAi => CodexCliService.SetupDocumentationUrl,
        AiProvider.Ollama => "https://docs.ollama.com/quickstart",
        _ => ClaudeCliService.SetupDocumentationUrl
    };
    public string TermsUrl => Provider switch { AiProvider.Claude => ClaudeCliService.LegalAndComplianceUrl, AiProvider.Ollama => "https://ollama.com/terms", _ => "https://openai.com/policies/terms-of-use/" };

    /// <summary>Momento de la última comprobación terminada, para no repetirla en cada navegación.</summary>
    public DateTime LastCheckedUtc { get; private set; } = DateTime.MinValue;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsNotInstalled), nameof(IsSignedOut), nameof(IsUnsupported),
        nameof(IsKnown), nameof(ShowChecking), nameof(StateLabel), nameof(AccountLabel), nameof(HasAccount),
        nameof(VersionLabel), nameof(ExecutablePathLabel), nameof(UnsupportedMessage), nameof(SupportsVaultTools))]
    private ClaudeCliStatus _status;

    /// <summary>El CLI deja trabajar al asistente dentro del vault (ver
    /// <see cref="ClaudeCliStatus.SupportsVaultTools"/>); si no, el asistente solo responde con texto.</summary>
    public bool SupportsVaultTools => Status.SupportsVaultTools;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChecking))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isChecking;

    public bool IsReady => Status.State == ClaudeCliState.Ready;
    public bool IsNotInstalled => Status.State == ClaudeCliState.NotInstalled;
    public bool IsSignedOut => Status.State == ClaudeCliState.SignedOut;
    public bool IsUnsupported => Status.State == ClaudeCliState.Unsupported;
    public bool IsKnown => Status.State != ClaudeCliState.Unknown;

    /// <summary>Primera comprobación aún en curso: todavía no se sabe qué enseñar.</summary>
    public bool ShowChecking => IsChecking && !IsKnown;

    public string StateLabel => Provider != AiProvider.Claude
        ? ProviderLabel + " · " + LocalizationService.Instance[IsReady ? "Ai_Ready" : IsNotInstalled ? "Ai_NotInstalled" : IsSignedOut ? "Ai_SignedOut" : IsUnsupported ? "Ai_Unavailable" : "Ai_Checking"]
        : LocalizationService.Instance[Status.State switch
    {
        ClaudeCliState.Ready => "Assistant_State_Ready",
        ClaudeCliState.NotInstalled => "Assistant_State_NotInstalled",
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
    }
}
