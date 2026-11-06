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
    private readonly SnoozedReminderService _snoozedReminderService;
    private string? _openedCardId;
    private string? _openedReminderId;

    public ReminderInboxViewModelTests()
    {
        _kanbanService = new KanbanService(_storageDirectory);
        _reminderService = new ReminderService(_storageDirectory);
        _settingsService = new SettingsService(_storageDirectory);
        _dismissedReminderService = new DismissedReminderService(_storageDirectory);
        _snoozedReminderService = new SnoozedReminderService(_storageDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ReminderInboxViewModel CreateViewModel() =>
        new(_kanbanService, _reminderService, _settingsService, _dismissedReminderService, _snoozedReminderService,
            id => _openedCardId = id, id => _openedReminderId = id);

    private KanbanCard AddDueCard(string title = "Tarjeta")
    {
        var card = new KanbanCard { Id = Guid.NewGuid().ToString("N"), Title = title, DueDateUtc = DateTime.UtcNow.AddHours(-1) };
        _kanbanService.DefaultBoard.Columns[0].Cards.Add(card);
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
        _kanbanService.DefaultBoard.Columns[0].Cards.Add(completed);
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

    // ===================== Aplazar (T5, .docs/funcionalidades-pendientes-priorizadas.md §4.1) =====================

    [Fact]
    public void SnoozePrimary_MovesTheItemOutOfOverdue_WithoutTouchingTheCard()
    {
        var card = AddDueCard();
        var originalDue = card.DueDateUtc;
        var vm = CreateViewModel();

        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        var item = vm.Items.Single(i => i.SourceId == card.Id);
        Assert.True(item.IsSnoozed);
        Assert.Equal(ReminderBucket.Snoozed, item.Bucket);
        Assert.Equal(0, vm.OverdueCount);
        // Criterio EARS: aplazar no reprograma la entidad de origen.
        Assert.Equal(originalDue, card.DueDateUtc);
    }

    [Fact]
    public void SnoozePrimary_UsesTheConfiguredQuickValue()
    {
        _settingsService.Settings.SnoozePrimaryMinutes = 25;
        var card = AddDueCard();
        var vm = CreateViewModel();
        var before = DateTime.UtcNow;

        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        var until = vm.Items.Single(i => i.SourceId == card.Id).SnoozedUntilUtc!.Value;
        Assert.InRange(until, before.AddMinutes(25), DateTime.UtcNow.AddMinutes(25).AddSeconds(5));
    }

    [Fact]
    public void SnoozeCustom_UsesTheEnteredMinutes_AndRemembersThemInSettings()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        vm.ToggleSnoozeMenuCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));
        vm.CustomSnoozeMinutes = 45;

        vm.SnoozeCustomCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        Assert.Equal(45, _settingsService.Settings.SnoozeCustomMinutes);
        var until = vm.Items.Single(i => i.SourceId == card.Id).SnoozedUntilUtc!.Value;
        Assert.InRange(until, DateTime.UtcNow.AddMinutes(44), DateTime.UtcNow.AddMinutes(46));
    }

    [Fact]
    public void SnoozeTwice_CountsTheRepeatOnTheItem()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();

        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));
        vm.SnoozeSecondaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        var item = vm.Items.Single(i => i.SourceId == card.Id);
        Assert.Equal(2, item.SnoozeCount);
        Assert.True(item.HasSnoozeCount);
    }

    [Fact]
    public void CancelSnooze_BringsTheItemBackToItsRealBucket()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        vm.CancelSnoozeCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        var item = vm.Items.Single(i => i.SourceId == card.Id);
        Assert.False(item.IsSnoozed);
        Assert.Equal(ReminderBucket.Overdue, item.Bucket);
        Assert.Equal(1, vm.OverdueCount);
    }

    /// <summary>Aplazar un recordatorio recurrente no puede tocar su <c>NotifyAtUtc</c>: ese campo ya
    /// apunta a la siguiente ocurrencia de la serie.</summary>
    [Fact]
    public void Snooze_OnARecurringReminder_LeavesTheSeriesAlone()
    {
        var reminder = _reminderService.Create("Semanal", string.Empty, DateTime.UtcNow.AddMinutes(-5),
            "BellRingOutline", recurrence: PolarRecurrence.Weekly);
        var vm = CreateViewModel();

        vm.SnoozeSecondaryCommand.Execute(vm.Items.Single(i => i.SourceId == reminder.Id));

        Assert.Equal(PolarRecurrence.Weekly, reminder.Recurrence);
        Assert.Equal(reminder.NotifyAtUtc, _reminderService.Reminders.Single(r => r.Id == reminder.Id).NotifyAtUtc);
        Assert.True(vm.Items.Single(i => i.SourceId == reminder.Id).IsSnoozed);
    }

    [Fact]
    public void Dismiss_AlsoDropsAPendingSnooze()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        vm.DismissCommand.Execute(vm.Items.Single(i => i.SourceId == card.Id));

        Assert.Null(_snoozedReminderService.Find(card.Id, card.DueDateUtc!.Value));
    }

    [Fact]
    public void ToggleSnoozeMenu_OpensOnlyOneMenuAtATime()
    {
        var first = AddDueCard("Primera");
        var second = AddDueCard("Segunda");
        var vm = CreateViewModel();

        vm.ToggleSnoozeMenuCommand.Execute(vm.Items.Single(i => i.SourceId == first.Id));
        vm.ToggleSnoozeMenuCommand.Execute(vm.Items.Single(i => i.SourceId == second.Id));

        Assert.False(vm.Items.Single(i => i.SourceId == first.Id).IsSnoozeMenuOpen);
        Assert.True(vm.Items.Single(i => i.SourceId == second.Id).IsSnoozeMenuOpen);
    }

    [Fact]
    public void SnoozedItems_SortAfterTheOnesStillDemandingAttention()
    {
        var snoozed = AddDueCard("Aplazada");
        var pending = AddDueCard("Pendiente");
        var vm = CreateViewModel();

        vm.SnoozePrimaryCommand.Execute(vm.Items.Single(i => i.SourceId == snoozed.Id));

        Assert.Equal(pending.Id, vm.Items[0].SourceId);
        Assert.Equal(snoozed.Id, vm.Items[^1].SourceId);
    }

    [Theory]
    [InlineData(10, "10 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    public void FormatDuration_ReadsAsADuration_NotAsRawMinutes(int minutes, string expected)
    {
        Assert.Equal(expected, ReminderInboxViewModel.FormatDuration(minutes));
    }
}
