using System;
using System.Collections.Generic;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>Puente estrecho entre el render visual y el VM: resolver y abrir notas sin dar al control
/// acceso directo al vault. La instancia vacía hace que un editor sin DataContext degrade con seguridad.</summary>
internal sealed record MarkdownTransclusionContext(
    Func<string, IReadOnlyList<string>?, NoteTransclusionResult>? Resolve,
    Action<string>? Open,
    string? CurrentNotePath)
{
    public static MarkdownTransclusionContext Empty { get; } = new(null, null, null);
}
