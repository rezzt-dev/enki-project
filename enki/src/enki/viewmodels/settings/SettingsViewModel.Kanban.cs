using System;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly KanbanService? _kanbanService;
    private readonly Action? _onManageKanbanBoards;
    public bool KanbanMultipleBoardsEnabled
    {
        get => _settingsService.Settings.KanbanMultipleBoardsEnabled;
        set
        {
            if (!value && _kanbanService?.Boards.Count > 1)
            {
                _notificationService.Push(LocalizationService.Instance["Kanban_Multi_Boards"],
                    LocalizationService.Instance["Kanban_Multi_DisableBlocked"], NotificationSeverity.Warning);
                OnPropertyChanged();
                return;
            }
            _settingsService.Settings.KanbanMultipleBoardsEnabled = value;
            _settingsService.Save();
            OnPropertyChanged();
        }
    }
    [RelayCommand] private void ManageKanbanBoards() => _onManageKanbanBoards?.Invoke();
}
