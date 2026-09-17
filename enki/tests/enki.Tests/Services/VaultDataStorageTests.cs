using System;
using System.IO;
using enki.Models;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Comprueba la separación por vault y la importación única de datos anteriores.</summary>
public sealed class VaultDataStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-vault-data-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private VaultService CreateVault()
    {
        var vault = new VaultService(new SettingsService(Path.Combine(_root, "config")));
        vault.InitializeVault(Path.Combine(_root, "vault-a"), seedWelcomeNote: false);
        return vault;
    }

    [Fact]
    public void TaskData_StaysWithItsVault_WhenSwitching()
    {
        var vault = CreateVault();
        var settings = new SettingsService(Path.Combine(_root, "notifications-config"));
        var kanban = new KanbanService(vault);
        var polar = new PolarTaskService(vault);
        var reminders = new ReminderService(vault);
        var dismissed = new DismissedReminderService(vault);
        var notifications = new NotificationService(settings, vault);

        kanban.Board.Name = "Vault A";
        kanban.Save();
        polar.Store.Lists.Add(new PolarTaskList { Id = "list-a", Title = "Lista A" });
        polar.Save();
        reminders.Create("Aviso A", "", DateTime.UtcNow.AddDays(1), "bell");
        dismissed.Dismiss("card-a", DateTime.UtcNow.Date);
        notifications.Push("Notificación A");

        Assert.True(File.Exists(Path.Combine(vault.VaultPath, ".tasks", "kanban.json")));
        Assert.True(File.Exists(Path.Combine(vault.VaultPath, ".tasks", "polar-tasks.json")));
        Assert.True(File.Exists(Path.Combine(vault.VaultPath, ".tasks", "reminders.json")));
        Assert.True(File.Exists(Path.Combine(vault.VaultPath, ".tasks", "dismissed-reminders.json")));
        Assert.True(File.Exists(Path.Combine(vault.VaultPath, ".enki", "notifications.json")));

        vault.SetVaultPath(Path.Combine(_root, "vault-b"));
        kanban.Reload();
        polar.Reload();
        reminders.Reload();
        dismissed.Reload();
        notifications.Reload();

        Assert.NotEqual("Vault A", kanban.Board.Name);
        Assert.Empty(polar.Store.Lists);
        Assert.Empty(reminders.Reminders);
        Assert.False(dismissed.IsDismissed("card-a", DateTime.UtcNow.Date));
        Assert.Empty(notifications.Notifications);

        vault.SetVaultPath(Path.Combine(_root, "vault-a"));
        kanban.Reload();
        polar.Reload();
        reminders.Reload();
        dismissed.Reload();
        notifications.Reload();

        Assert.Equal("Vault A", kanban.Board.Name);
        Assert.Contains(polar.Store.Lists, list => list.Id == "list-a");
        Assert.Contains(reminders.Reminders, reminder => reminder.Title == "Aviso A");
        Assert.True(dismissed.IsDismissed("card-a", DateTime.UtcNow.Date));
        Assert.Contains(notifications.Notifications, notification => notification.Title == "Notificación A");
    }

    [Fact]
    public void Migration_CopiesOnce_AndPreservesExistingVaultFiles()
    {
        var vault = CreateVault();
        var legacy = Path.Combine(_root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "kanban.json"), "legacy board");
        File.WriteAllText(Path.Combine(legacy, "reminders.json"), "legacy reminders");
        Directory.CreateDirectory(Path.Combine(vault.VaultPath, ".tasks"));
        File.WriteAllText(Path.Combine(vault.VaultPath, ".tasks", "kanban.json"), "existing board");

        VaultDataStorage.MigrateLegacyFiles(vault, legacy);

        Assert.Equal("existing board", File.ReadAllText(Path.Combine(vault.VaultPath, ".tasks", "kanban.json")));
        Assert.Equal("legacy reminders", File.ReadAllText(Path.Combine(vault.VaultPath, ".tasks", "reminders.json")));
        Assert.Equal("legacy board", File.ReadAllText(Path.Combine(legacy, "kanban.json")));

        vault.SetVaultPath(Path.Combine(_root, "vault-b"));
        VaultDataStorage.MigrateLegacyFiles(vault, legacy);
        Assert.False(File.Exists(Path.Combine(vault.VaultPath, ".tasks", "reminders.json")));
    }

    [Fact]
    public void PolarCursor_IsScopedToEachVault()
    {
        var vault = CreateVault();
        var settings = new VaultSettingsService(vault);
        settings.Settings.PolarLastSyncAtUtcMs = 1234;
        settings.Save();

        vault.SetVaultPath(Path.Combine(_root, "vault-b"));
        settings.Reload();
        Assert.Equal(0, settings.Settings.PolarLastSyncAtUtcMs);

        vault.SetVaultPath(Path.Combine(_root, "vault-a"));
        settings.Reload();
        Assert.Equal(1234, settings.Settings.PolarLastSyncAtUtcMs);
    }
}
