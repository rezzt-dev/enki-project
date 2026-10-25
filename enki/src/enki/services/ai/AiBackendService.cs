using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using enki.Models;

namespace enki.Services;

/// <summary>Selecciona el transporte del asistente sin duplicar historial, permisos ni herramientas.
/// Los contratos de turno originales se comparten con los nuevos transportes.</summary>
public sealed class AiBackendService
{
    private readonly ClaudeCliService _claude;
    private readonly CodexCliService _codex;
    private readonly OllamaService _ollama;
    private int _generation;

    public AiBackendService(ClaudeCliService claude, SettingsService settings, string? storageDirectory = null,
        OllamaService? ollama = null)
    {
        _claude = claude;
        _codex = new CodexCliService(settings, storageDirectory ?? claude.StorageDirectory);
        _ollama = ollama ?? new OllamaService(settings);
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
    public IReadOnlyList<string> LocalModels => _ollama.Models;
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
