using System;
using System.IO;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class KanbanCardViewModelTests
{
    private static KanbanCard NewCard(string id = "id", string title = "Card",
        KanbanPriority priority = KanbanPriority.Medium) =>
        new() { Id = id, Title = title, Priority = priority };

    [Fact]
    public void OpeningOnly_IsVisibleInMetadata_AndDoesNotMakeCardOverdue()
    {
        var card = NewCard();
        card.StartDateUtc = DateTime.Today.AddDays(-7).ToUniversalTime();
        var vm = new KanbanCardViewModel(card);
        Assert.True(vm.HasDueDate);
        Assert.True(vm.HasStatusMetadata);
        Assert.False(vm.IsOverdue);
        Assert.Contains(KanbanCard.FormatDueLabel(card.StartDateUtc.Value), vm.DueDateLabel);
        card.DueDateUtc = DateTime.Today.AddDays(7).ToUniversalTime();
        vm.RefreshFromModel();
        Assert.Contains(KanbanCard.FormatDueLabel(card.DueDateUtc.Value), vm.DueDateLabel);
    }

    [Fact]
    public void CyclePriority_GoesLowMediumHighLow()
    {
        var card = NewCard();
        card.Priority = KanbanPriority.Low;
        var vm = new KanbanCardViewModel(card);

        vm.CyclePriority();
        Assert.Equal(KanbanPriority.Medium, vm.Priority);

        vm.CyclePriority();
        Assert.Equal(KanbanPriority.High, vm.Priority);

        vm.CyclePriority();
        Assert.Equal(KanbanPriority.Low, vm.Priority);
    }

    [Fact]
    public void MatchesTags_NoSelection_AlwaysMatches()
    {
        var vm = new KanbanCardViewModel(NewCard());
        Assert.True(vm.MatchesTags([]));
    }

    [Fact]
    public void MatchesTags_CaseInsensitive()
    {
        var card = NewCard();
        card.Tags = ["Urgente"];
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.MatchesTags(["urgente"]));
        Assert.False(vm.MatchesTags(["otra"]));
    }

    [Fact]
    public void HasLinkedNote_DerivesFileNameLabel_FromPath()
    {
        var card = NewCard();
        card.LinkedNotePath = "/vault/carpeta/Mi Nota.md";
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.HasLinkedNote);
        Assert.Equal("Mi Nota", vm.LinkedNoteLabel);
    }

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 5, análisis §4 — un enlace nota↔tarjeta a una nota
    /// que ya no existe debe distinguirse (icono/tooltip), en vez de descubrirse solo al hacer clic.</summary>
    [Fact]
    public void IsLinkedNoteMissing_TrueWhenLinkedFileDoesNotExistOnDisk()
    {
        var card = NewCard();
        card.LinkedNotePath = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N") + ".md");
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.IsLinkedNoteMissing);
        Assert.NotNull(vm.LinkedNoteTooltip);
        Assert.Equal(Material.Icons.MaterialIconKind.NoteAlertOutline, vm.LinkedNoteIcon);
    }

    [Fact]
    public void IsLinkedNoteMissing_FalseWhenLinkedFileExists()
    {
        var path = Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N") + ".md");
        File.WriteAllText(path, "# Nota");
        try
        {
            var card = NewCard();
            card.LinkedNotePath = path;
            var vm = new KanbanCardViewModel(card);

            Assert.False(vm.IsLinkedNoteMissing);
            Assert.Null(vm.LinkedNoteTooltip);
            Assert.Equal(Material.Icons.MaterialIconKind.NoteTextOutline, vm.LinkedNoteIcon);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void HasLinkedNote_False_WhenNoNoteLinked_LinkedNoteTooltipIsNull()
    {
        var vm = new KanbanCardViewModel(NewCard());

        Assert.False(vm.HasLinkedNote);
        Assert.False(vm.IsLinkedNoteMissing);
        Assert.Null(vm.LinkedNoteTooltip);
    }

    [Fact]
    public void HasSubtasks_And_SubtaskProgressLabel_ReflectDoneCount()
    {
        var card = NewCard();
        card.Subtasks =
        [
            new KanbanSubtask { Id = "s1", Title = "a", IsDone = true },
            new KanbanSubtask { Id = "s2", Title = "b", IsDone = false }
        ];
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.HasSubtasks);
        Assert.Equal("1/2", vm.SubtaskProgressLabel);
    }

    [Fact]
    public void MatchesSearch_EmptyQuery_AlwaysMatches()
    {
        var vm = new KanbanCardViewModel(NewCard());
        Assert.True(vm.MatchesSearch(string.Empty));
    }

    [Fact]
    public void MatchesSearch_MatchesTitleOrDescription_CaseInsensitive()
    {
        var card = NewCard(title: "Revisar contrato");
        card.Description = "Con el cliente nuevo";
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.MatchesSearch("CONTRATO"));
        Assert.True(vm.MatchesSearch("cliente"));
        Assert.False(vm.MatchesSearch("factura"));
    }

    [Fact]
    public void MatchesSearch_MatchesSubtaskTitle_CaseInsensitive()
    {
        var card = NewCard(title: "Contrato");
        card.Description = "nada";
        card.Subtasks.Add(new KanbanSubtask { Id = "s1", Title = "Revisar cláusula", IsDone = false });
        var vm = new KanbanCardViewModel(card);

        Assert.True(vm.MatchesSearch("cláusula"));
        Assert.False(vm.MatchesSearch("factura"));
    }

    [Fact]
    public void SubtaskProgressStars_AreBothZero_WithoutSubtasks()
    {
        var vm = new KanbanCardViewModel(NewCard());

        Assert.Equal(0, vm.SubtaskProgressDoneStar.Value);
        Assert.Equal(0, vm.SubtaskProgressRemainingStar.Value);
    }

    [Fact]
    public void SubtaskProgressStars_SplitTheTrack_ByDoneFraction()
    {
        var card = NewCard();
        card.Subtasks =
        [
            new KanbanSubtask { Id = "s1", Title = "a", IsDone = true },
            new KanbanSubtask { Id = "s2", Title = "b", IsDone = false },
            new KanbanSubtask { Id = "s3", Title = "c", IsDone = false }
        ];
        var vm = new KanbanCardViewModel(card);

        Assert.Equal(1, vm.SubtaskProgressDoneStar.Value);
        Assert.Equal(2, vm.SubtaskProgressRemainingStar.Value);
        Assert.True(vm.SubtaskProgressDoneStar.IsStar);
        Assert.True(vm.SubtaskProgressRemainingStar.IsStar);
    }

    [Fact]
    public void IsHighPriority_OnlyTrue_ForHighPriority()
    {
        Assert.True(new KanbanCardViewModel(NewCard(priority: KanbanPriority.High)).IsHighPriority);
        Assert.False(new KanbanCardViewModel(NewCard(priority: KanbanPriority.Medium)).IsHighPriority);
        Assert.False(new KanbanCardViewModel(NewCard(priority: KanbanPriority.Low)).IsHighPriority);
    }

    [Fact]
    public void HasMeta_IsFalse_ForACardWithOnlyATitle()
    {
        Assert.False(new KanbanCardViewModel(NewCard()).HasMeta);
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void HasMeta_IsTrue_WhenAnySingleMetadataFieldIsSet(bool subtask, bool dueDate, bool tag, bool linkedNote)
    {
        var card = NewCard();
        if (subtask) card.Subtasks.Add(new KanbanSubtask { Id = "s1", Title = "a" });
        if (dueDate) card.DueDateUtc = DateTime.UtcNow.AddDays(3);
        if (tag) card.Tags.Add("urgente");
        if (linkedNote) card.LinkedNotePath = "/vault/nota.md";

        Assert.True(new KanbanCardViewModel(card).HasMeta);
    }

    [Fact]
    public void MatchesQuickFilter_None_LetsEveryCardThrough()
    {
        var vm = new KanbanCardViewModel(NewCard(priority: KanbanPriority.Low));
        Assert.True(vm.MatchesQuickFilter(KanbanQuickFilter.None));
    }

    [Fact]
    public void MatchesQuickFilter_HighPriority_KeepsOnlyHighPriorityCards()
    {
        Assert.True(new KanbanCardViewModel(NewCard(priority: KanbanPriority.High))
            .MatchesQuickFilter(KanbanQuickFilter.HighPriority));
        Assert.False(new KanbanCardViewModel(NewCard(priority: KanbanPriority.Medium))
            .MatchesQuickFilter(KanbanQuickFilter.HighPriority));
    }

    [Fact]
    public void MatchesQuickFilter_Overdue_KeepsOnlyPastDueCards()
    {
        var overdue = NewCard();
        overdue.DueDateUtc = DateTime.UtcNow.AddDays(-2);
        var upcoming = NewCard();
        upcoming.DueDateUtc = DateTime.UtcNow.AddDays(5);

        Assert.True(new KanbanCardViewModel(overdue).MatchesQuickFilter(KanbanQuickFilter.Overdue));
        Assert.False(new KanbanCardViewModel(upcoming).MatchesQuickFilter(KanbanQuickFilter.Overdue));
        Assert.False(new KanbanCardViewModel(NewCard()).MatchesQuickFilter(KanbanQuickFilter.Overdue));
    }

    [Fact]
    public void RefreshFromModel_RaisesPropertyChanged_ForDerivedProperties()
    {
        var card = NewCard();
        var vm = new KanbanCardViewModel(card);

        var raised = new System.Collections.Generic.List<string>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        card.Title = "Nuevo título";
        vm.RefreshFromModel();

        Assert.Contains(nameof(KanbanCardViewModel.Title), raised);
    }
}
