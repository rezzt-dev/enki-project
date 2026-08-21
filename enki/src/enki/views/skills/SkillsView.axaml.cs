using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using enki.ViewModels;

namespace enki.Views;

public partial class SkillsView : UserControl
{
    public SkillsView()
    {
        InitializeComponent();

        // The card grid's column count (see ApplySkillsGridColumns) is computed from the available
        // width, so it has to be recomputed whenever that width changes — otherwise a WrapPanel-style
        // fixed card width would leave a ragged gap on the right of every row instead of the cards
        // stretching to fill it.
        SkillsGridBorder.SizeChanged += (_, _) => ApplySkillsGridColumns();
    }

    private SkillsViewModel? Vm => DataContext as SkillsViewModel;

    /// <summary>Picks how many equal-width columns the card grid should have so cards stretch to fill
    /// each row edge-to-edge (248px is the minimum readable card width before adding another column).
    /// The UniformGrid lives inside an ItemsPanelTemplate, which has its own template namescope, so its
    /// x:Name never binds to a field on this class — it has to be fetched through the ItemsControl's
    /// realized Presenter.Panel instead.</summary>
    private void ApplySkillsGridColumns()
    {
        if (SkillsItemsControl.Presenter?.Panel is not UniformGrid grid) return;

        const double cardMinWidth = 248;
        const double cardSpacing = 16;
        var chrome = SkillsItemsControl.Margin.Left + SkillsItemsControl.Margin.Right;
        var available = SkillsGridBorder.Bounds.Width - chrome;
        grid.Columns = available > 0
            ? Math.Max(1, (int)((available + cardSpacing) / (cardMinWidth + cardSpacing)))
            : 1;
    }

    private void NamePromptTextBox_Initialized(object? sender, System.EventArgs e)
    {
        if (sender is not TextBox textBox) return;
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    private void NamePromptTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Vm?.ConfirmNamePromptCommand.Execute(null);
                break;
            case Key.Escape:
                e.Handled = true;
                Vm?.CancelNamePromptCommand.Execute(null);
                break;
        }
    }
}
