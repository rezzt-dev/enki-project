using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>One row of the sidebar "etiquetas" filter (docs/calendar-feature/07-filtros-busqueda-y-capas.md
/// §3) — only shown when <c>AppSettings.PolarLinked</c>, one per distinct tag currently used across
/// <c>PolarTask.Tags</c>. Same toggle-with-callback shape as <see cref="CalendarLayerViewModel"/>, kept as
/// its own type rather than reused because a tag has no color swatch and its selection isn't persisted
/// across sessions (docs/calendar-feature/07-filtros-busqueda-y-capas.md only requires persisting the
/// layer combination, §1 — not the secondary filters).</summary>
public sealed partial class CalendarTagFilterViewModel : ObservableObject
{
    private readonly Action<CalendarTagFilterViewModel> _onToggled;

    public string Tag { get; }

    [ObservableProperty]
    private bool _isActive;

    public CalendarTagFilterViewModel(string tag, bool isActive, Action<CalendarTagFilterViewModel> onToggled)
    {
        Tag = tag;
        _isActive = isActive;
        _onToggled = onToggled;
    }

    partial void OnIsActiveChanged(bool value) => _onToggled(this);
}
