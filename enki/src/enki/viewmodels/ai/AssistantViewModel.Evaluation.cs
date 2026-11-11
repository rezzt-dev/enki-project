using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class AssistantViewModel
{
    private AiEvaluationService Evaluations => new(Path.GetDirectoryName(_settingsService.SettingsFilePath));
    private CancellationTokenSource? _evaluationCancellation;

    [ObservableProperty]
    private bool _isEvaluationOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunEvaluation), nameof(CanClearEvaluations))]
    [NotifyCanExecuteChangedFor(nameof(RunEvaluationCommand), nameof(ClearEvaluationsCommand))]
    private bool _isEvaluating;

    [ObservableProperty]
    private string _evaluationStatus = string.Empty;

    public ObservableCollection<string> EvaluationResults { get; } = [];
    public bool HasEvaluationResults => EvaluationResults.Count > 0;
    public bool CanClearEvaluations => HasEvaluationResults && !IsEvaluating;
    public bool CanRunEvaluation => Cli.IsReady && VaultToolsAvailable && !IsSending && !IsEvaluating;

    [RelayCommand]
    private void OpenEvaluation()
    {
        IsEvaluationOpen = true;
        LoadEvaluations();
        OnPropertyChanged(nameof(CanRunEvaluation));
        RunEvaluationCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void CloseEvaluation()
    {
        _evaluationCancellation?.Cancel();
        IsEvaluationOpen = false;
    }

    private void LoadEvaluations()
    {
        try
        {
            EvaluationResults.Clear();
            foreach (var result in Evaluations.Load())
            {
                var model = result.Model.Length > 0 ? result.Model : LocalizationService.Instance["Assistant_Evaluation_DefaultModel"];
                EvaluationResults.Add($"{result.CreatedUtc.ToLocalTime():g} · {result.Provider} / {model} · {result.Score}/100 · v{result.SuiteVersion}\n"
                    + string.Join("\n", result.Tasks.Select(t => $"{t.Task}: {t.Score}/100 · {t.SuccessfulCalls}/{t.SuccessfulCalls + t.FailedCalls} · {t.ElapsedMilliseconds} ms · {t.InputTokens}/{t.OutputTokens}"
                        + (t.Failure == ClaudeFailureKind.None ? string.Empty : " · " + t.Failure))));
            }
            OnPropertyChanged(nameof(HasEvaluationResults));
            OnPropertyChanged(nameof(CanClearEvaluations));
            ClearEvaluationsCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            EvaluationStatus = LocalizedTextFormatter.Resolve(VaultFailureReasons.DescribeLocalized(ex));
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunEvaluation))]
    private async Task RunEvaluationAsync()
    {
        var backend = _cli.Fork();
        var model = SelectedModel?.Alias ?? string.Empty;
        using var cancellation = new CancellationTokenSource();
        _evaluationCancellation = cancellation;
        IsEvaluating = true;
        try
        {
            await Evaluations.RunAsync(backend.Provider, model,
                (request, token) => backend.RunTurnAsync(request, null, token),
                work => Dispatcher.UIThread.InvokeAsync(work),
                index => _dispatch(() => EvaluationStatus = string.Format(LocalizationService.Instance.CurrentCulture,
                    LocalizationService.Instance["Assistant_Evaluation_Progress"], index + 1, 2)), cancellation.Token);
            EvaluationStatus = LocalizationService.Instance["Assistant_Evaluation_Success"];
            LoadEvaluations();
        }
        catch (OperationCanceledException)
        {
            EvaluationStatus = LocalizationService.Instance["Assistant_Evaluation_Cancelled"];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException
            or System.Net.Sockets.SocketException)
        {
            EvaluationStatus = LocalizedTextFormatter.Resolve(VaultFailureReasons.DescribeLocalized(ex));
        }
        finally
        {
            IsEvaluating = false;
            _evaluationCancellation = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClearEvaluations))]
    private void ClearEvaluations()
    {
        try { Evaluations.Clear(); LoadEvaluations(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { EvaluationStatus = LocalizedTextFormatter.Resolve(VaultFailureReasons.DescribeLocalized(ex)); }
    }
}
