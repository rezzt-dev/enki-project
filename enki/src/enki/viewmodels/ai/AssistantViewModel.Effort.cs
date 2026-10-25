using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;

namespace enki.ViewModels;

/// <summary>Esfuerzo de razonamiento independiente por conversación, aplicado al siguiente turno.</summary>
public sealed partial class AssistantViewModel
{
    [ObservableProperty]
    private bool _isEffortPickerOpen;

    [RelayCommand]
    private void ToggleEffortPicker() => IsEffortPickerOpen = !IsEffortPickerOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffortLabel), nameof(SelectedEffort), nameof(EffortSummary))]
    private int _effortLevel = 1;

    public string SelectedEffort => EffortLevel switch { 0 => "low", 2 => "high", _ => "medium" };

    public string EffortLabel => LocalizationService.Instance["Assistant_Effort_" + SelectedEffort];

    public string EffortSummary => ShowEffortUnavailable
        ? LocalizationService.Instance["Assistant_Model_Default"] : EffortLabel;

    public bool CanChangeEffort => Cli.IsReady && Cli.Status.SupportsEffort && !IsSending;

    public bool ShowEffortUnavailable => Cli.IsReady && !Cli.Status.SupportsEffort;

    partial void OnEffortLevelChanged(int value)
    {
        if (value is < 0 or > 2)
        {
            EffortLevel = System.Math.Clamp(value, 0, 2);
            return;
        }
        if (_activeSession is null)
        {
            ScheduleDraftSave();
            return;
        }
        if (_activeSession.Effort == SelectedEffort) return;
        _activeSession.Effort = SelectedEffort;
        TrySaveSession(_activeSession, touch: false);
    }

    private void RestoreEffort(string? effort) =>
        EffortLevel = effort switch { "low" => 0, "high" => 2, _ => 1 };
}
