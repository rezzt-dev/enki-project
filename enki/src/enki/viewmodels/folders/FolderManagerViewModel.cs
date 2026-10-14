using System;
using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed record FolderDestination(string FullPath, string Label);

/// <summary>Panel común para organizar las carpetas reales desde Notas y Archivos.</summary>
public sealed partial class FolderManagerViewModel(VaultService vault, NotificationService? notifications) : ObservableObject
{
    private string _source = string.Empty;
    private string _root = string.Empty;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _error = string.Empty;
    [ObservableProperty] private string _operation = "Rename";
    [ObservableProperty] private FolderDestination? _destination;
    public ObservableCollection<FolderDestination> Destinations { get; } = [];
    public string RelativePath => string.IsNullOrEmpty(_source) ? string.Empty : Path.GetRelativePath(_root, _source);
    public bool IsCreate => Operation == "Create";
    public bool NeedsName => Operation is "Create" or "Rename";
    public bool NeedsDestination => Operation is "Copy" or "Move";
    public bool IsDelete => Operation == "Delete";
    public bool IsRename => Operation == "Rename";
    public bool IsMove => Operation == "Move";
    public bool IsCopy => Operation == "Copy";
    public string Title => LocalizationService.Instance[IsCreate ? "Folders_Create" : "Folders_Manage"];
    public string ConfirmLabel => LocalizationService.Instance["Folders_" + Operation];
    public string Summary { get; private set; } = string.Empty;

    public void Open(string source, string root, bool create = false)
    {
        IsOpen = false;
        try
        {
            vault.ValidateFolderPath(root, allowRoot: true);
            if (!VaultService.IsWithinFolder(source, root))
                throw new IOException(LocalizationService.Instance["Folders_Error_OutsideVault"]);
            vault.ValidateFolderPath(source, allowRoot: create);
            _source = source;
            _root = root;
            Name = create ? string.Empty : Path.GetFileName(source);
            Operation = create ? "Create" : "Rename";
            Error = string.Empty;
            Destinations.Clear();
            if (!create) AddDestination(new NoteFolder { FullPath = root, Children = vault.GetFolderTree(root) });
            Destination = null;
            Summary = create ? LocalizationService.Instance["Folders_CreateHint"] : LocalizationService.Instance["Folders_ContentsHint"];
            OnPropertyChanged(nameof(RelativePath));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Title));
            IsOpen = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            notifications?.Push(LocalizationService.Instance["Folders_Error"], ex.Message, NotificationSeverity.Error);
        }
    }

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ConfirmLabel));
        Summary = LocalizationService.Instance[IsCreate ? "Folders_CreateHint" : "Folders_ContentsHint"];
        OnPropertyChanged(nameof(Summary));
        var selected = Destination?.FullPath;
        for (var i = 0; i < Destinations.Count; i++)
            if (VaultService.PathsEqual(Destinations[i].FullPath, _root))
                Destinations[i] = new FolderDestination(_root, LocalizationService.Instance["Folders_Root"]);
        Destination = System.Linq.Enumerable.FirstOrDefault(Destinations, d => d.FullPath == selected);
    }

    private void AddDestination(NoteFolder folder)
    {
        var path = folder.FullPath;
        if (VaultService.IsWithinFolder(path, _source)) return;
        Destinations.Add(new FolderDestination(path, VaultService.PathsEqual(path, _root)
            ? LocalizationService.Instance["Folders_Root"] : Path.GetRelativePath(_root, path)));
        foreach (var child in folder.Children) AddDestination(child);
    }

    partial void OnOperationChanged(string value)
    {
        Error = string.Empty;
        foreach (var property in new[] { nameof(IsCreate), nameof(NeedsName), nameof(NeedsDestination), nameof(IsDelete), nameof(IsRename), nameof(IsMove), nameof(IsCopy), nameof(ConfirmLabel), nameof(Title) })
            OnPropertyChanged(property);
        ApplyCommand.NotifyCanExecuteChanged();
    }
    partial void OnIsOpenChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnNameChanged(string value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnDestinationChanged(FolderDestination? value) => ApplyCommand.NotifyCanExecuteChanged();

    [RelayCommand] private void SelectOperation(string operation) => Operation = operation;
    [RelayCommand] private void Close() => IsOpen = false;
    [RelayCommand] private void Reveal() => FolderOpener.Open(_source);
    private bool CanApply => IsOpen && (NeedsName ? !string.IsNullOrWhiteSpace(Name) : !NeedsDestination || Destination is not null);

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        if (!CanApply) return;
        try
        {
            NoteFolder? result = null;
            switch (Operation)
            {
                case "Create": result = vault.CreateManagedFolder(_source, Name); break;
                case "Rename": result = vault.RenameManagedFolder(_source, Name); break;
                case "Copy": result = vault.TransferFolder(_source, Destination!.FullPath, false); break;
                case "Move": result = vault.TransferFolder(_source, Destination!.FullPath, true); break;
                case "Delete": vault.DeleteFolder(new NoteFolder { FullPath = _source }); break;
                default: return;
            }
            IsOpen = false;
            notifications?.Push(LocalizationService.Instance["Folders_Done"], result?.Name ?? Path.GetFileName(_source), NotificationSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            Error = ex.Message;
            notifications?.Push(LocalizationService.Instance["Folders_Error"], Error, NotificationSeverity.Error);
        }
    }
}
