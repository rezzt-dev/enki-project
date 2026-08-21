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
    private readonly SettingsService _settingsService;
    private readonly DismissedReminderService _dismissedReminderService;
    private string? _openedCardId;

    public ReminderInboxViewModelTests()
    {
        _kanbanService = new KanbanService(_storageDirectory);
        _settingsService = new SettingsService(_storageDirectory);
        _dismissedReminderService = new DismissedReminderService(_storageDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ReminderInboxViewModel CreateViewModel() =>
        new(_kanbanService, _settingsService, _dismissedReminderService, id => _openedCardId = id);

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
        var item = vm.Items.Single(i => i.CardId == card.Id);

        vm.DismissCommand.Execute(item);

        Assert.DoesNotContain(vm.Items, i => i.CardId == card.Id);
        Assert.True(_dismissedReminderService.IsDismissed(card.Id, card.DueDateUtc!.Value));
        Assert.True(vm.HasLastDismissed);
        Assert.NotNull(vm.LastDismissed);
        Assert.Equal(card.Id, vm.LastDismissed!.CardId);
    }

    [Fact]
    public void UndoDismiss_ClearsTheDismissal_AndBringsTheItemBack()
    {
        var card = AddDueCard();
        var vm = CreateViewModel();
        var item = vm.Items.Single(i => i.CardId == card.Id);
        vm.DismissCommand.Execute(item);

        vm.UndoDismissCommand.Execute(null);

        Assert.False(vm.HasLastDismissed);
        Assert.Null(vm.LastDismissed);
        Assert.False(_dismissedReminderService.IsDismissed(card.Id, card.DueDateUtc!.Value));
        Assert.Contains(vm.Items, i => i.CardId == card.Id);
    }

    [Fact]
    public void UndoDismiss_WithNothingDismissed_DoesNothing()
    {
        var vm = CreateViewModel();

        vm.UndoDismissCommand.Execute(null);

        Assert.False(vm.HasLastDismissed);
    }

    [Fact]
    public void Dismiss_ASecondCard_ReplacesTheUndoTarget_WithoutUndismissingTheFirst()
    {
        var first = AddDueCard("Primera");
        var second = AddDueCard("Segunda");
        var vm = CreateViewModel();

        vm.DismissCommand.Execute(vm.Items.Single(i => i.CardId == first.Id));
        vm.DismissCommand.Execute(vm.Items.Single(i => i.CardId == second.Id));

        Assert.Equal(second.Id, vm.LastDismissed!.CardId);

        vm.UndoDismissCommand.Execute(null);

        // Solo la segunda (la que estaba en LastDismissed) vuelve — la primera sigue descartada.
        Assert.Contains(vm.Items, i => i.CardId == second.Id);
        Assert.DoesNotContain(vm.Items, i => i.CardId == first.Id);
    }
}
