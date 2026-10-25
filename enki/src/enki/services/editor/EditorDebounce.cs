using System;

namespace enki.Services;

/// <summary>Single source of truth for how long the editor waits after the last keystroke before doing
/// anything expensive in response to it — autosave (<c>EditorViewModel</c>), the backlinks panel
/// (<c>EditorViewModel.OnTextChanged</c>) and the outline/stats side panels
/// (<c>EditorOutlineViewModel</c>/<c>EditorStatsViewModel</c>) all used to recompute on every single
/// keystroke via <c>TextDocument.TextChanged</c>, which on a large vault meant a full rescan for each
/// character typed (docs/analisis-proyecto/roadmap.md, Fase 6, análisis §3). Sharing one constant instead
/// of four independent 600ms literals keeps them tuned together if this value ever changes.</summary>
public static class EditorDebounce
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(600);
}
