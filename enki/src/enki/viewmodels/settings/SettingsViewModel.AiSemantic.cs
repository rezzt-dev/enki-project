using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Inteligencia artificial: revisión de cambios por lotes (AIA-07) y búsqueda semántica local
/// (AIA-16) — encenderla, elegir el modelo de embeddings de Ollama, ver el estado del índice, reconstruirlo y
/// borrarlo. Ver <c>.docs/project-info/asistente-ia.md</c>.</summary>
public sealed partial class SettingsViewModel
{
    private AiSemanticIndexService? _aiSemanticIndex;

    /// <summary>Sin servicio (pruebas que no componen el asistente) la tarjeta no se enseña.</summary>
    public bool ShowAiSemanticSettings => _aiSemanticIndex is not null;

    /// <summary>Los cambios de contenido de notas que pide el asistente esperan a que el usuario los apruebe.</summary>
    [ObservableProperty]
    private bool _aiReviewChangesBeforeApplying;

    [ObservableProperty]
    private bool _aiSemanticSearchEnabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAiEmbeddingModelCommand))]
    private string _aiEmbeddingModel = string.Empty;

    private void InitializeAiSemantic(AiSemanticIndexService? semanticIndex)
    {
        _aiSemanticIndex = semanticIndex;
        if (semanticIndex is not null)
            semanticIndex.StatusChanged += () => Dispatcher.UIThread.Post(RefreshAiSemanticStatus);
        LoadAiSemanticSettings();
    }

    private void LoadAiSemanticSettings()
    {
        var wasLoading = _loadingAiSettings;
        _loadingAiSettings = true;
        try
        {
            var ai = _settingsService.Settings.Ai;
            AiReviewChangesBeforeApplying = ai.ReviewChangesBeforeApplying;
            AiSemanticSearchEnabled = ai.SemanticSearchEnabled;
            AiEmbeddingModel = ai.EmbeddingModel;
        }
        finally
        {
            _loadingAiSettings = wasLoading;
        }
        RefreshAiSemanticStatus();
    }

    /// <summary>Se lee al empezar cada turno: el que esté en curso sigue como empezó.</summary>
    partial void OnAiReviewChangesBeforeApplyingChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.ReviewChangesBeforeApplying = value;
        _settingsService.Save();
    }

    /// <summary>Encenderla construye el índice en segundo plano; apagarla deja de usarlo, pero no lo borra: para eso
    /// está "Borrar índice".</summary>
    partial void OnAiSemanticSearchEnabledChanged(bool value)
    {
        if (_loadingAiSettings) return;
        _settingsService.Settings.Ai.SemanticSearchEnabled = value;
        _settingsService.Save();
        _aiSemanticIndex?.SettingsChanged();
        RefreshAiSemanticStatus();
    }

    private bool CanApplyAiEmbeddingModel() => !string.IsNullOrWhiteSpace(AiEmbeddingModel)
        && !string.Equals(AiEmbeddingModel.Trim(), _settingsService.Settings.Ai.EmbeddingModel, StringComparison.Ordinal);

    /// <summary>Otro modelo no se puede mezclar con los vectores del anterior: el índice se reconstruye entero.</summary>
    [RelayCommand(CanExecute = nameof(CanApplyAiEmbeddingModel))]
    private void ApplyAiEmbeddingModel()
    {
        _settingsService.Settings.Ai.EmbeddingModel = AiEmbeddingModel.Trim();
        _settingsService.Save();
        ApplyAiEmbeddingModelCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(AiEmbeddingInstallCommand));
        _aiSemanticIndex?.SettingsChanged();
    }

    [RelayCommand]
    private void RebuildAiSemanticIndex()
    {
        if (_aiSemanticIndex is null || !AiSemanticSearchEnabled) return;
        _ = _aiSemanticIndex.RebuildAsync();
    }

    [RelayCommand]
    private void DeleteAiSemanticIndex()
    {
        if (_aiSemanticIndex is null) return;
        _aiSemanticIndex.DeleteIndex();
        _notificationService.PushLocalized(LocalizedTextFormatter.Format("Settings_Ai_Semantic_Deleted_Title"),
            LocalizedTextFormatter.Format("Settings_Ai_Semantic_Deleted_Message"), NotificationSeverity.Success);
    }

    /// <summary>Orden para instalar el modelo configurado en Ollama, para copiarla en una terminal.</summary>
    public string AiEmbeddingInstallCommand => "ollama pull " + _settingsService.Settings.Ai.EmbeddingModel;

    public bool IsAiSemanticIndexing => _aiSemanticIndex?.Status.State == AiSemanticIndexState.Indexing;

    public bool HasAiSemanticProblem => _aiSemanticIndex?.Status.State == AiSemanticIndexState.Error;

    /// <summary>Falta el modelo: se enseña la orden para instalarlo.</summary>
    public bool ShowAiEmbeddingInstallCommand => _aiSemanticIndex?.Status.Problem == AiSemanticProblem.ModelMissing;

    /// <summary>"Índice al día · 312 notas, 1.204 fragmentos · actualizado hace 5 min", "Indexando… quedan 40 notas" o
    /// el motivo del error, en el idioma vigente.</summary>
    public string AiSemanticStatusLabel
    {
        get
        {
            if (_aiSemanticIndex?.Status is not { } status) return string.Empty;
            var text = LocalizationService.Instance;
            var culture = text.CurrentCulture;
            string Counts() => string.Format(culture, text["Settings_Ai_Semantic_Counts"],
                status.Notes.ToString("N0", culture), status.Chunks.ToString("N0", culture));
            return status.State switch
            {
                AiSemanticIndexState.Off => text["Settings_Ai_Semantic_Status_Off"],
                AiSemanticIndexState.Empty => text["Settings_Ai_Semantic_Status_Empty"],
                AiSemanticIndexState.Indexing => string.Format(culture, text["Settings_Ai_Semantic_Status_Indexing"],
                    status.Pending.ToString("N0", culture)),
                AiSemanticIndexState.Error => text["Settings_Ai_Semantic_Problem_" + (status.Problem ?? AiSemanticProblem.Failed)]
                    + (status.Notes > 0 ? " · " + Counts() : string.Empty),
                _ => string.Format(culture, text["Settings_Ai_Semantic_Status_Ready"], Counts(),
                    status.UpdatedUtc is { } updated ? RelativeTimeFormatter.Format(updated) : "—")
            };
        }
    }

    private void RefreshAiSemanticStatus()
    {
        OnPropertyChanged(nameof(AiSemanticStatusLabel));
        OnPropertyChanged(nameof(IsAiSemanticIndexing));
        OnPropertyChanged(nameof(HasAiSemanticProblem));
        OnPropertyChanged(nameof(ShowAiEmbeddingInstallCommand));
        OnPropertyChanged(nameof(AiEmbeddingInstallCommand));
    }

    private void RefreshAiSemanticLocalizedLabels() => RefreshAiSemanticStatus();
}
