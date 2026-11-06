using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Input;

namespace enki.Services;

/// <summary>Live-updating registry of effective keyboard gestures (propuesta 29,
/// docs/propuesta-nuevas-funcionalidades.md) — every view that wires up a remappable <c>KeyBinding</c> or
/// checks a key combo in code-behind (<c>MainWindow</c>, <c>NotesView</c>, <c>RemindersView</c>,
/// <c>TrashView</c>, <c>EditorView</c>) reads the effective gesture for a <see cref="ShortcutIds"/> constant
/// from here instead of hardcoding it, and subscribes to <see cref="Changed"/> to rebuild its bindings the
/// moment Ajustes › Atajos remaps or resets one. Same "static live mirror" shape as
/// <see cref="EditorPreferences"/>, except this one also owns the override dictionary that gets persisted
/// (via <c>Models.AppSettings.ShortcutOverrides</c>) instead of being purely in-memory.</summary>
public static class ShortcutRegistry
{
    public static event Action? Changed;

    private static readonly Dictionary<string, KeyGesture> Overrides = new();

    /// <summary>Loads persisted overrides at startup — see
    /// <c>MainWindowViewModel.Composition.cs</c>. Malformed entries (a gesture string that no longer parses,
    /// e.g. after an Avalonia upgrade changed its format) are skipped rather than throwing, falling back to
    /// that shortcut's default gesture. Does not raise <see cref="Changed"/>, since nothing has subscribed
    /// yet this early in startup.</summary>
    public static void Initialize(IReadOnlyDictionary<string, string> overrides)
    {
        Overrides.Clear();
        foreach (var (id, gestureText) in overrides)
        {
            if (TryParse(gestureText, out var gesture)) Overrides[id] = gesture;
        }
    }

    public static KeyGesture GetGesture(string id)
    {
        if (Overrides.TryGetValue(id, out var overridden)) return overridden;

        var definition = ShortcutCatalog.All.First(d => d.Id == id);
        return KeyGesture.Parse(definition.DefaultGesture);
    }

    /// <summary>Human-friendly rendering of a gesture for Ajustes › Atajos — "Ctrl+1" / "Ctrl+," instead
    /// of the raw <c>KeyGesture.ToString()</c> output ("Ctrl+D1" / "Ctrl+OemComma"), which is meant for
    /// round-tripping through <see cref="KeyGesture.Parse(string)"/>, not for display. Persistence
    /// (<see cref="ExportOverrides"/>) still uses the raw format, since that one has to parse back.</summary>
    public static string GetGestureDisplay(string id)
    {
        var gesture = GetGesture(id);
        var modifiers = FormatModifiers(gesture.KeyModifiers);
        var key = FormatKey(gesture.Key);
        return modifiers.Length == 0 ? key : $"{modifiers}+{key}";
    }

    private static string FormatModifiers(KeyModifiers modifiers)
    {
        var parts = new List<string>(4);
        if ((modifiers & KeyModifiers.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        if ((modifiers & KeyModifiers.Meta) != 0) parts.Add("Cmd");
        return string.Join("+", parts);
    }

    private static string FormatKey(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((int)(key - Key.D0)).ToString(CultureInfo.InvariantCulture);
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (key - Key.NumPad0);

        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.Add => "Num+",
            Key.Subtract => "Num-",
            Key.Escape => "Esc",
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            _ => key.ToString()
        };
    }

    public static bool IsCustomized(string id) => Overrides.ContainsKey(id);

    public static bool Matches(string id, KeyEventArgs e) => GetGesture(id).Matches(e);

    /// <summary>Returns the id of another shortcut that already resolves to <paramref name="gesture"/>, or
    /// null if none does — used by Ajustes › Atajos to warn before saving a remap that collides with
    /// another action (propuesta 29 §"Detección de conflictos").</summary>
    public static string? FindConflict(string id, KeyGesture gesture)
    {
        foreach (var definition in ShortcutCatalog.All)
        {
            if (definition.Id == id) continue;
            if (GestureEquals(GetGesture(definition.Id), gesture)) return definition.Id;
        }
        return null;
    }

    public static void SetOverride(string id, KeyGesture gesture)
    {
        Overrides[id] = gesture;
        Changed?.Invoke();
    }

    public static void ResetToDefault(string id)
    {
        if (!Overrides.Remove(id)) return;
        Changed?.Invoke();
    }

    public static void ResetAll()
    {
        if (Overrides.Count == 0) return;
        Overrides.Clear();
        Changed?.Invoke();
    }

    /// <summary>Snapshot of the current overrides in the same "Ctrl+Shift+F" text format used everywhere
    /// else, ready to assign to <c>AppSettings.ShortcutOverrides</c> before <c>SettingsService.Save()</c>.</summary>
    public static IReadOnlyDictionary<string, string> ExportOverrides() =>
        Overrides.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    private static bool GestureEquals(KeyGesture a, KeyGesture b) => a.Key == b.Key && a.KeyModifiers == b.KeyModifiers;

    private static bool TryParse(string text, out KeyGesture gesture)
    {
        try
        {
            gesture = KeyGesture.Parse(text);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or FormatException)
        {
            gesture = default!;
            return false;
        }
    }
}
