using System;
using System.IO;
using enki.Models;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

public class KanbanCardViewModelTests
{
    private static KanbanCard NewCard(string id = "id", string title = "Card") =>
        new() { Id = id, Title = title };

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
    public void SubtaskProgressWidth_IsZero_WithoutSubtasks()
    {
        var vm = new KanbanCardViewModel(NewCard());
        Assert.Equal(0, vm.SubtaskProgressWidth);
    }

    [Fact]
    public void SubtaskProgressWidth_ScalesWithDoneFraction()
    {
        var card = NewCard();
        card.Subtasks =
        [
            new KanbanSubtask { Id = "s1", Title = "a", IsDone = true },
            new KanbanSubtask { Id = "s2", Title = "b", IsDone = false }
        ];
        var vm = new KanbanCardViewModel(card);

        Assert.Equal(27, vm.SubtaskProgressWidth);
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
