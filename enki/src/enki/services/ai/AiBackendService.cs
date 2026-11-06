using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Modelo que ofrece un proveedor con catálogo propio (Ollama, Gemini) para los selectores.</summary>
public sealed record AiModelEntry(string Id, string Label);

/// <summary>Selecciona el transporte del asistente sin duplicar historial, permisos ni herramientas.
/// Los contratos de turno originales se comparten con los nuevos transportes.</summary>
public sealed class AiBackendService
{
    private readonly ClaudeCliService _claude;
    private readonly CodexCliService _codex;
    private readonly OllamaService _ollama;
    private readonly GeminiApiService _gemini;
    private int _generation;

    public AiBackendService(ClaudeCliService claude, SettingsService settings, string? storageDirectory = null,
        OllamaService? ollama = null, GeminiApiService? gemini = null)
    {
        _claude = claude;
        _codex = new CodexCliService(settings, storageDirectory ?? claude.StorageDirectory);
        _ollama = ollama ?? new OllamaService(settings);
        // El almacén no se abre hasta que se usa Gemini: quien no lo elige nunca ve un aviso del llavero.
        _gemini = gemini ?? new GeminiApiService(settings,
            new AiApiKeyStore(storageDirectory ?? claude.StorageDirectory, AiApiKeyKind.Gemini));
        Provider = settings.Settings.Ai.Provider;
        Status = Provider == AiProvider.Claude ? claude.Status : ClaudeCliStatus.Unknown;
        claude.StatusChanged += status =>
        {
            if (Provider == AiProvider.Claude) Publish(status);
        };
    }

    public AiProvider Provider { get; private set; }
    public string? Model { get; set; }
    public ClaudeCliStatus Status { get; private set; }

    /// <summary>Modelos descubiertos en la última comprobación del proveedor activo. Vacío para los CLI, que
    /// usan alias fijos o el modelo que el usuario escriba.</summary>
    public IReadOnlyList<AiModelEntry> Models => Provider switch
    {
        AiProvider.Ollama => _ollama.Models.Select(m => new AiModelEntry(m, m)).ToArray(),
        AiProvider.Gemini => _gemini.Models.Select(m => new AiModelEntry(m.Id, m.DisplayName)).ToArray(),
        _ => []
    };

    /// <summary>Clave de Google AI Studio, compartida con Ajustes.</summary>
    public AiApiKeyStore GeminiApiKeys => _gemini.ApiKeys;
    public event Action<ClaudeCliStatus>? StatusChanged;
    public event Action? ProviderChanged;
    public event Action? BusyChanged;
    public bool IsBusy { get; private set; }

    public void SelectProvider(AiProvider provider)
    {
        if (IsBusy || !Enum.IsDefined(provider) || Provider == provider) return;
        Provider = provider;
        Model = null;
        _generation++;
        Publish(ClaudeCliStatus.Unknown);
        ProviderChanged?.Invoke();
    }

    private void Publish(ClaudeCliStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(status);
    }

    public async Task<ClaudeCliStatus> DetectAsync()
    {
        var generation = _generation;
        var provider = Provider;
        var status = provider switch
        {
            AiProvider.OpenAi => await _codex.DetectAsync().ConfigureAwait(false),
            AiProvider.Ollama => await _ollama.DetectAsync(Model).ConfigureAwait(false),
            AiProvider.Gemini => await _gemini.DetectAsync().ConfigureAwait(false),
            _ => await _claude.DetectAsync().ConfigureAwait(false)
        };
        if (generation == _generation && provider != AiProvider.Claude) Publish(status);
        return status;
    }

    public async Task<ClaudeTurnResult> RunTurnAsync(ClaudeTurnRequest request, Action<ClaudeStreamEvent>? onEvent,
        CancellationToken cancellation)
    {
        IsBusy = true;
        BusyChanged?.Invoke();
        try
        {
            return Provider switch
            {
                AiProvider.OpenAi => await _codex.RunTurnAsync(request, onEvent, cancellation).ConfigureAwait(false),
                AiProvider.Ollama => await _ollama.RunTurnAsync(request, onEvent, cancellation).ConfigureAwait(false),
                AiProvider.Gemini => await _gemini.RunTurnAsync(request, onEvent, cancellation).ConfigureAwait(false),
                _ => await _claude.RunTurnAsync(request, onEvent, cancellation).ConfigureAwait(false)
            };
        }
        finally
        {
            IsBusy = false;
            BusyChanged?.Invoke();
        }
    }
}
