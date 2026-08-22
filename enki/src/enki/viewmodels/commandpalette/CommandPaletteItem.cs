using System;
using Material.Icons;

namespace enki.ViewModels;

/// <summary>A single action registered with the command palette (Ctrl+Shift+K) — a title, an icon and the
/// delegate to run when selected. The full list is rebuilt every time the palette opens (see
/// MainWindowViewModel.BuildCommandPaletteItems) rather than cached once at startup, so entries that depend
/// on live state (which sidebar sections are enabled, whether a note tab is open, whether Polar is linked)
/// never go stale between one open and the next.</summary>
public sealed class CommandPaletteItem(string title, MaterialIconKind icon, Action execute)
{
    public string Title { get; } = title;
    public MaterialIconKind Icon { get; } = icon;
    public Action Execute { get; } = execute;
}
