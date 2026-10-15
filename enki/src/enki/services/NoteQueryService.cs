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
        else if (!string.IsNullOrWhiteSpace(query.DatePropertyKey) &&
                 !System.Text.RegularExpressions.Regex.IsMatch(query.DatePropertyKey.Trim(), "^[A-Za-z][A-Za-z0-9_-]*$"))
            error = LocalizationService.Instance["Notes_Query_InvalidDateProperty"];
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
             (string.IsNullOrWhiteSpace(query.PropertyValue) ||
              (query.ExactPropertyValue
                  ? value.Equals(query.PropertyValue.Trim(), StringComparison.OrdinalIgnoreCase)
                  : value.Contains(query.PropertyValue.Trim(), StringComparison.OrdinalIgnoreCase)))) &&
            DateMatches(note, query, from, to));
    }

    private static bool DateMatches(NoteFile note, NoteQuery query, DateOnly? from, DateOnly? to)
    {
        if (from is null && to is null && string.IsNullOrWhiteSpace(query.DatePropertyKey)) return true;
        DateOnly date;
        if (string.IsNullOrWhiteSpace(query.DatePropertyKey))
            date = DateOnly.FromDateTime(note.ModifiedUtc.ToLocalTime());
        else if (!note.Properties.TryGetValue(query.DatePropertyKey.Trim(), out var raw) ||
                 !DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                     DateTimeStyles.None, out date))
            return false;
        return (from is null || date >= from) && (to is null || date <= to);
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
