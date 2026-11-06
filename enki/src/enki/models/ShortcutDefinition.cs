namespace enki.Models;

/// <summary>One remappable keyboard shortcut (propuesta 29, docs/propuesta-nuevas-funcionalidades.md) —
/// <see cref="Id"/> is the stable key stored in <see cref="AppSettings.ShortcutOverrides"/>,
/// <see cref="DefaultGesture"/> is the factory gesture in the same "Ctrl+Shift+F" text format
/// <c>Avalonia.Input.KeyGesture</c> itself parses and formats, and <see cref="CategoryKey"/>/
/// <see cref="LabelKey"/> are <c>Strings.resx</c> keys used to group and describe the shortcut in
/// Ajustes › Atajos.</summary>
public sealed record ShortcutDefinition(string Id, string DefaultGesture, string CategoryKey, string LabelKey);
