using System;
using System.IO;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public sealed class SidePanelLayoutViewModelTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    [Fact]
    public void SaveFailure_RestoresPositionAndNotifiesUser()
    {
        var service = new SettingsService(_directory);
        var notifications = new NotificationService(service, Path.Combine(_directory, "notifications"));
        var vm = new SidePanelLayoutViewModel(service, notifications);
        // Una carpeta en la ruta del JSON impide el reemplazo atómico sin depender de permisos del SO.
        Directory.CreateDirectory(service.SettingsFilePath);
        vm.NotesOnRight = true;
        Assert.False(vm.NotesOnRight);
        Assert.False(service.Settings.NotesPanelOnRight);
        Assert.Single(notifications.Notifications);
        Assert.Equal(enki.Models.NotificationSeverity.Error, notifications.Notifications[0].Severity);
    }

    [Fact]
    public void OldSettings_DefaultToLeft_AndChoicesPersistIndependently()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "settings.json"), "{}");
        var service = new SettingsService(_directory);
        var vm = new SidePanelLayoutViewModel(service, new NotificationService(service, _directory));
        Assert.False(vm.NotesOnRight);
        Assert.False(vm.FilesOnRight);
        Assert.False(vm.SkillsOnRight);
        Assert.False(vm.AssistantOnRight);
        vm.NotesOnRight = true;
        vm.SkillsOnRight = true;
        var reloaded = new SettingsService(_directory).Settings;
        Assert.True(reloaded.NotesPanelOnRight);
        Assert.False(reloaded.FilesPanelOnRight);
        Assert.True(reloaded.SkillsPanelOnRight);
        Assert.False(reloaded.AssistantPanelOnRight);
        vm.FilesOnRight = true;
        vm.AssistantOnRight = true;
        vm.NotesOnRight = false;
        reloaded = new SettingsService(_directory).Settings;
        Assert.False(reloaded.NotesPanelOnRight);
        Assert.True(reloaded.FilesPanelOnRight);
        Assert.True(reloaded.SkillsPanelOnRight);
        Assert.True(reloaded.AssistantPanelOnRight);
    }

    [Fact]
    public void SwitchingVault_ReloadsEachVaultPreference()
    {
        var service = new SettingsService(_directory);
        var first = Path.Combine(_directory, "first");
        var second = Path.Combine(_directory, "second");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        service.SwitchVault(first);
        var vm = new SidePanelLayoutViewModel(service, new NotificationService(service, _directory));
        vm.NotesOnRight = true;
        service.SwitchVault(second);
        vm.Reload();
        Assert.False(vm.NotesOnRight);
        vm.FilesOnRight = true;
        service.SwitchVault(first);
        vm.Reload();
        Assert.True(vm.NotesOnRight);
        Assert.False(vm.FilesOnRight);
    }
}
