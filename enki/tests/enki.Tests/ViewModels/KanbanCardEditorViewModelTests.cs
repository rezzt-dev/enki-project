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
}
