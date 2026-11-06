using System;
using Avalonia.Controls;
using Avalonia.Input;
using AvaloniaEdit;
using enki.Services;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Regresiones del teclado real: selección, autocierre y deshacer sobre AvaloniaEdit.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class EditorEditingUiTests : IDisposable
{
    private readonly Window _window;
    private readonly TextEditor _editor;
    private readonly bool _autoPairs;

    public EditorEditingUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _autoPairs = EditorPreferences.AutoPairs;
        EditorPreferences.AutoPairs = true;
        var view = new EditorView();
        _window = new Window { Content = view, Width = 1000, Height = 700 };
        _window.Show();
        _editor = view.FindControl<TextEditor>("Editor")!;
    }

    public void Dispose()
    {
        _window.Close();
        EditorPreferences.AutoPairs = _autoPairs;
    }

    private void Press(Key key) => _editor.TextArea.RaiseEvent(new KeyEventArgs
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = key,
        Source = _editor.TextArea
    });

    private void Type(string text) => _editor.TextArea.RaiseEvent(new TextInputEventArgs
    {
        RoutedEvent = InputElement.TextInputEvent,
        Text = text,
        Source = _editor.TextArea
    });

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Enter_RenumbersImmediatelyAndUndoesInOneStep(string newline)
    {
        var original = "1. uno" + newline + "2. dos";
        _editor.Text = original;
        _editor.CaretOffset = 6;
        _editor.Document.UndoStack.ClearAll();
        Press(Key.Enter);
        Assert.Equal("1. uno" + newline + "2. " + newline + "3. dos", _editor.Text);
        Assert.Equal(6 + newline.Length + 3, _editor.CaretOffset);
        _editor.Undo();
        Assert.Equal(original, _editor.Text);
        Assert.False(_editor.CanUndo);
    }

    [Fact]
    public void AutoPair_WrapsTheSelectionAndUndoesInOneStep()
    {
        _editor.Text = "texto";
        _editor.Select(0, 5);
        _editor.Document.UndoStack.ClearAll();
        Type("(");
        Assert.Equal("(texto)", _editor.Text);
        Assert.Equal("texto", _editor.SelectedText);
        _editor.Undo();
        Assert.Equal("texto", _editor.Text);
        Assert.False(_editor.CanUndo);
    }

    [Fact]
    public void TypingThreeBackticks_ProducesAnOpeningFence()
    {
        _editor.Text = string.Empty;
        Type("`");
        Type("`");
        Type("`");
        Assert.Equal("```", _editor.Text);
    }
}
