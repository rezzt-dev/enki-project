using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="ReminderInboxViewModel"/>'s "Deshacer" affordance (Fase 10,
/// docs/analisis-proyecto/roadmap.md — "undo al Dismiss de la bandeja de recordatorios del shell"). The
/// timed auto-hide itself (<c>UndoWindow</c>) isn't exercised here — it's a real
/// <c>DispatcherTimer</c>, and this project doesn't wait on wall-clock timers in tests — only the
/// synchronous state transitions (dismiss → undo bar appears → undo reverts it) are.</summary>
public class ReminderInboxViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly KanbanService _kanbanService;
    private readonly ReminderService _reminderService;
    private readonly SettingsService _settingsService;
    private readonly DismissedReminderService _dismissedReminderService;
    private string? _openedCardId;
    private string? _openedReminderId;

    public ReminderInboxViewModelTests()
    {
        _kanbanService = new KanbanService(_storageDirectory);
        _reminderService = new ReminderService(_storageDirectory);
        _settingsService = new SettingsService(_storageDirectory);
        _dismissedReminderService = new DismissedReminderService(_storageDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ReminderInboxViewModel CreateViewModel() =>
        new(_kanbanService, _reminderService, _settingsService, _dismissedReminderService,
            id => _openedCardId = id, id => _openedReminderId = id);

    private KanbanCard AddDueCard(string title = "Tarjeta")
    {
        var card = new KanbanCard { Id = Guid.NewGuid().ToString("N"), Title = title, DueDateUtc = DateTime.UtcNow.AddHours(-1) };
        _kanbanService.Board.Columns[0].Cards.Add(card);
        _kanbanService.Save();
        return card;
    }

    [Fact]
    public void Dismiss_RemovesItemFromInbox_AndShowsUndoBar()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        var item = vm.Items.Single(i => i.SourceId == card.Id);

        vm.DismissCommand.Execute(item);

        Assert.DoesNotContain(vm.Items, i => i.SourceId == card.Id);
        Assert.True(_dismissedReminderService.IsDismissed(card.Id, card.DueDateUtc!.Value));
        Assert.True(vm.HasLastDismissed);
        Assert.NotNull(vm.LastDismissed);
        Assert.Equal(card.Id, vm.LastDismissed!.SourceId);
    }

    [Fact]
    public void UndoDismiss_ClearsTheDismissal_AndBringsTheItemBack()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        var item = vm.Items.Single(i => i.SourceId == card.Id);
        vm.DismissCommand.Execute(item);

        vm.UndoDismissCommand.Execute(null);

        Assert.False(vm.HasLastDismissed);
        Assert.Null(vm.LastDismissed);
        Assert.False(_dismissedReminderService.IsDismissed(card.Id, card.DueDateUtc!.Value));
        Assert.Contains(vm.Items, i => i.SourceId == card.Id);
    }

    [Fact]
    public void UndoDismiss_WithNothingDismissed_DoesNothing()
    {
        var vm = CreateViewModel();

        vm.UndoDismissCommand.Execute(null);

        Assert.False(vm.HasLastDismissed);
    }

    // ===================== Fase 3: excluir tarjetas completadas (análisis §4.4, Alta) =====================

    [Fact]
    public void Reload_ExcludesCards_WithCompletedAtUtcSet()
    {
        var completed = new KanbanCard
        {
            Id = Guid.NewGuid().ToString("N"), Title = "Ya hecha",
            DueDateUtc = DateTime.UtcNow.AddHours(-1), CompletedAtUtc = DateTime.UtcNow.AddMinutes(-30)
        };
        _kanbanService.Board.Columns[0].Cards.Add(completed);
        _kanbanService.Save();
        var pending = AddDueCard("Pendiente");

        var vm = CreateViewModel();

        Assert.DoesNotContain(vm.Items, i => i.SourceId == completed.Id);
        Assert.Contains(vm.Items, i => i.SourceId == pending.Id);
        Assert.Equal(1, vm.OverdueCount);
    }

    [Fact]
    public void Dismiss_ASecondCard_ReplacesTheUndoTarget_WithoutUndismissingTheFirst()
    {
        var first = AddDueCard("Primera");
        var second = AddDueCard("Segunda");
        var vm = CreateViewModel();

        vm.DismissCommand.Execute(vm.Items.Single(i => i.SourceId == first.Id));
        vm.DismissCommand.Execute(vm.Items.Single(i => i.SourceId == second.Id));

        Assert.Equal(second.Id, vm.LastDismissed!.SourceId);

        vm.UndoDismissCommand.Execute(null);

        // Solo la segunda (la que estaba en LastDismissed) vuelve — la primera sigue descartada.
        Assert.Contains(vm.Items, i => i.SourceId == second.Id);
        Assert.DoesNotContain(vm.Items, i => i.SourceId == first.Id);
    }

    // ===================== Bandeja unificada: recordatorios propios (docs/agent-docs/
    // notificaciones-y-recordatorios.md) — el icono está rotulado "Recordatorios", así que también debe
    // reflejar los recordatorios creados en esa página, no solo las tarjetas de Kanban. =====================

    [Fact]
    public void Reload_IncludesStandaloneReminders_AlongsideKanbanCards()
    {
        var card = AddDueCard();
        var reminder = _reminderService.Create("Llamar al dentista", "", DateTime.UtcNow.AddHours(-1), "BellRingOutline");

        var vm = CreateViewModel();

        Assert.Contains(vm.Items, i => i.SourceId == card.Id && i.SourceKind == ReminderItemSourceKind.KanbanCard);
        Assert.Contains(vm.Items, i => i.SourceId == reminder.Id && i.SourceKind == ReminderItemSourceKind.Reminder);
        Assert.Equal(2, vm.OverdueCount);
    }

    [Fact]
    public void Reload_ExcludesCompletedStandaloneReminders()
    {
        var reminder = _reminderService.Create("Ya hecho", "", DateTime.UtcNow.AddHours(-1), "BellRingOutline");
        _reminderService.ToggleCompleted(reminder.Id);

        var vm = CreateViewModel();

        Assert.DoesNotContain(vm.Items, i => i.SourceId == reminder.Id);
    }

    [Fact]
    public void OpenCard_OnStandaloneReminder_InvokesTheReminderCallback_NotTheCardCallback()
    {
        var reminder = _reminderService.Create("Llamar al dentista", "", DateTime.UtcNow.AddHours(-1), "BellRingOutline");
        var vm = CreateViewModel();
        var item = vm.Items.Single(i => i.SourceId == reminder.Id);

        vm.OpenCardCommand.Execute(item);

        Assert.Equal(reminder.Id, _openedReminderId);
        Assert.Null(_openedCardId);
    }

    [Fact]
    public void Dismiss_OnStandaloneReminder_RemovesItFromInbox()
    {
        var reminder = _reminderService.Create("Llamar al dentista", "", DateTime.UtcNow.AddHours(-1), "BellRingOutline");
        var vm = CreateViewModel();
        var item = vm.Items.Single(i => i.SourceId == reminder.Id);

        vm.DismissCommand.Execute(item);

        Assert.DoesNotContain(vm.Items, i => i.SourceId == reminder.Id);
        Assert.True(_dismissedReminderService.IsDismissed(reminder.Id, reminder.NotifyAtUtc));
    }
}
