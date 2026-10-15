using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using enki.Models;
using enki.Localization;

namespace enki.Services;

/// <summary>Evalúa consultas sobre metadatos de las notas reales, sin índice persistente.</summary>
public static class NoteQueryService
{
    public static bool TryValidate(NoteQuery query, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(query.PropertyKey) && !string.IsNullOrWhiteSpace(query.PropertyValue))
            error = LocalizationService.Instance["Notes_Query_PropertyRequired"];
        else if (!TryDate(query.ModifiedFrom, out _)) error = LocalizationService.Instance["Notes_Query_InvalidFrom"];
        else if (!TryDate(query.ModifiedTo, out _)) error = LocalizationService.Instance["Notes_Query_InvalidTo"];
        else if (TryDate(query.ModifiedFrom, out var from) && TryDate(query.ModifiedTo, out var to) && from > to)
            error = LocalizationService.Instance["Notes_Query_InvalidRange"];
        return error.Length == 0;
    }

    public static IEnumerable<NoteFile> Filter(IEnumerable<NoteFile> notes, NoteQuery query)
    {
        if (!TryValidate(query, out _)) return [];
        TryDate(query.ModifiedFrom, out var from);
        TryDate(query.ModifiedTo, out var to);
        return notes.Where(note =>
            (string.IsNullOrWhiteSpace(query.Tag) || note.Tags.Contains(query.Tag.Trim(), StringComparer.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(query.PropertyKey) ||
             note.Properties.TryGetValue(query.PropertyKey.Trim(), out var value) &&
             (string.IsNullOrWhiteSpace(query.PropertyValue) || value.Contains(query.PropertyValue.Trim(), StringComparison.OrdinalIgnoreCase))) &&
            (from is null || DateOnly.FromDateTime(note.ModifiedUtc.ToLocalTime()) >= from) &&
            (to is null || DateOnly.FromDateTime(note.ModifiedUtc.ToLocalTime()) <= to));
    }

    private static bool TryDate(string text, out DateOnly? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        value = date;
        return true;
    }
}
