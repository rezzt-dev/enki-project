using System;
using System.Collections.Generic;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class KanbanCardEditorViewModelTests
{
    private static KanbanCardEditorViewModel CreateEditor(string tagsInput)
    {
        var column = new KanbanColumn { Id = "c1", Title = "Column" };
        var columnVm = new KanbanColumnViewModel(column);
        var card = new KanbanCard { Id = "id", Title = "Card", Tags = [] };
        var cardVm = new KanbanCardViewModel(card);
        var editor = KanbanCardEditorViewModel.ForEdit(columnVm, cardVm, [columnVm]);
        editor.TagsInput = tagsInput;
        return editor;
    }

    [Fact]
    public void BuildTags_TrimAndDeduplicatesCaseInsensitive()
    {
        var editor = CreateEditor("  Casa , casa , URGENTE , ");
        var tags = editor.BuildTags();
        Assert.Equal(["Casa", "URGENTE"], tags);
        Assert.Equal(2, tags.Length);
    }

    [Fact]
    public void BuildCard_IncludesTags()
    {
        var editor = CreateEditor("casa, urgente");
        var card = editor.BuildCard("new-id");
        Assert.Equal(new List<string> { "casa", "urgente" }, card.Tags);
    }

    [Fact]
    public void ForEdit_PreservesExistingLinkedNotePath()
    {
        var column = new KanbanColumn { Id = "c1", Title = "Column" };
        var columnVm = new KanbanColumnViewModel(column);
        var card = new KanbanCard { Id = "id", Title = "Card", LinkedNotePath = "/vault/Nota.md" };
        var cardVm = new KanbanCardViewModel(card);

        var editor = KanbanCardEditorViewModel.ForEdit(columnVm, cardVm, [columnVm]);

        Assert.True(editor.HasLinkedNote);
        Assert.Equal("/vault/Nota.md", editor.LinkedNotePath);
    }

    [Fact]
    public void ClearLinkedNoteCommand_ClearsLinkedNotePath()
    {
        var editor = CreateEditor(string.Empty);
        editor.LinkedNotePath = "/vault/Nota.md";

        editor.ClearLinkedNoteCommand.Execute(null);

        Assert.False(editor.HasLinkedNote);
        Assert.Null(editor.LinkedNotePath);
    }

    [Fact]
    public void BuildCard_And_ApplyTo_CarryLinkedNotePath()
    {
        var editor = CreateEditor(string.Empty);
        editor.LinkedNotePath = "/vault/Nota.md";

        var built = editor.BuildCard("new-id");
        Assert.Equal("/vault/Nota.md", built.LinkedNotePath);

        var existing = new KanbanCard { Id = "id", Title = "Card" };
        editor.ApplyTo(existing);
        Assert.Equal("/vault/Nota.md", existing.LinkedNotePath);
    }

    // ===================== CombinedDueDate (privado, ejercitado vía BuildCard) =====================

    [Fact]
    public void BuildCard_WithoutDueDate_LeavesDueDateUtcNull()
    {
        var editor = CreateEditor(string.Empty);

        var built = editor.BuildCard("new-id");

        Assert.Null(built.DueDateUtc);
    }

    [Fact]
    public void BuildCard_WithDueDateButNoDueTime_UsesLocalMidnight()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = new DateTime(2026, 3, 15);
        editor.HasDueTime = false;

        var built = editor.BuildCard("new-id");

        Assert.NotNull(built.DueDateUtc);
        var local = built.DueDateUtc!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 3, 15), local.Date);
        Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
    }

    [Fact]
    public void BuildCard_WithDueTime_CombinesDateAndTimeOfDay()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = new DateTime(2026, 3, 15);
        editor.HasDueTime = true;
        editor.DueHour = 14;
        editor.DueMinute = 30;

        var built = editor.BuildCard("new-id");

        var local = built.DueDateUtc!.Value.ToLocalTime();
        Assert.Equal(new DateTime(2026, 3, 15, 14, 30, 0), local);
    }

    // ===================== OnHasDueTimeChanged =====================

    [Fact]
    public void TurningOffHasDueTime_ClearsHasDuration()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = DateTime.Today;
        editor.HasDueTime = true;
        editor.HasDuration = true;

        editor.HasDueTime = false;

        Assert.False(editor.HasDuration);
    }

    [Fact]
    public void TurningOnHasDueTime_DoesNotForceHasDurationOn()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = DateTime.Today;

        editor.HasDueTime = true;

        Assert.False(editor.HasDuration);
    }

    // ===================== MaxDurationMinutes (privado, ejercitado vía el clamp de DurationMinutes) =====================

    [Fact]
    public void SettingDueHourNearMidnight_ClampsDurationMinutesToWhatFitsBeforeMidnight()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = DateTime.Today;
        editor.HasDueTime = true;
        editor.HasDuration = true;
        editor.DurationMinutes = 60;

        editor.DueHour = 23;
        editor.DueMinute = 55;

        Assert.Equal(5, editor.DurationMinutes);
    }

    [Fact]
    public void SettingDueHourEarlyInTheDay_LeavesDurationMinutesUnclamped()
    {
        var editor = CreateEditor(string.Empty);
        editor.DueDate = DateTime.Today;
        editor.HasDueTime = true;
        editor.HasDuration = true;
        editor.DurationMinutes = 60;

        editor.DueHour = 9;
        editor.DueMinute = 0;

        Assert.Equal(60, editor.DurationMinutes);
    }
}
