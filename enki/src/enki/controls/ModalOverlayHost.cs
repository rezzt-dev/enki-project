using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace enki.Controls;

/// <summary>
/// Wraps a "modal card" (confirmation dialogs, editors, detail panels) in a <see cref="Popup"/>
/// anchored to the window's <see cref="TopLevel"/> instead of the page it's declared in — the
/// previous pattern (a plain <c>Border Classes="modal-overlay"</c> nested inside each page's own
/// Grid) could never visually escape that page's bounds, so the sidebar and the custom title bar's
/// floating buttons (search, notifications, minimize/maximize/close) stayed clickable underneath it.
/// Sizing/placement is done in code-behind against <see cref="TopLevel.Bounds"/> rather than XAML
/// bindings because the popup's content detaches into the window's overlay layer once open, and a
/// compiled <c>$parent[Window]</c> binding path is not guaranteed to resolve across both the
/// ControlTemplate boundary and that detach in the same hop.
/// </summary>
public sealed class ModalOverlayHost : ContentControl
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<ModalOverlayHost, bool>(nameof(IsOpen));

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    private Popup? _popup;
    private TopLevel? _topLevel;
    private IDisposable? _boundsSubscription;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _popup = e.NameScope.Find<Popup>("PART_Popup");
        AttachToTopLevel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AttachToTopLevel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _boundsSubscription?.Dispose();
        _boundsSubscription = null;
        _topLevel = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachToTopLevel()
    {
        var topLevel = this.FindAncestorOfType<TopLevel>();
        if (topLevel is null || _popup is null || ReferenceEquals(topLevel, _topLevel))
        {
            return;
        }

        _boundsSubscription?.Dispose();
        _topLevel = topLevel;
        _popup.PlacementTarget = topLevel;
        SyncSize();
        _boundsSubscription = topLevel.GetObservable(TopLevel.BoundsProperty).Subscribe(_ => SyncSize());
    }

    private void SyncSize()
    {
        if (_popup is null || _topLevel is null)
        {
            return;
        }

        _popup.Width = _topLevel.Bounds.Width;
        _popup.Height = _topLevel.Bounds.Height;
    }
}
