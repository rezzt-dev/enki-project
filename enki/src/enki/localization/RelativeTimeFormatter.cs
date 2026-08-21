using System;

namespace enki.Localization;

/// <summary>Single localized implementation of the "hace 2 min"/"2 min ago" pattern that today is
/// duplicated, hardcoded in Spanish, and never pluralized in 5 places (<c>NoteFile.cs</c>,
/// <c>VaultFile.cs</c>, <c>TrashedReminderCardViewModel.cs</c>, <c>TrashedTaskCardViewModel.cs</c>,
/// <c>NotificationItemViewModel.cs</c>) — see docs/agent-docs/internacionalizacion-i18n.md §4.7. Fase 2
/// wires the 5 call sites to this formatter instead of their own copy; this class is infrastructure only for
/// now and isn't referenced anywhere yet.</summary>
public static class RelativeTimeFormatter
{
    public static string Format(DateTime utc) => Format(utc, DateTime.UtcNow);

    public static string Format(DateTime utc, DateTime nowUtc)
    {
        var delta = nowUtc - utc;
        if (delta.TotalMinutes < 1) return LocalizationService.Instance["Common_RelativeTime_JustNow"];
        if (delta.TotalMinutes < 60)
        {
            var minutes = (int)delta.TotalMinutes;
            return Plural.Get("Common_RelativeTime_MinutesAgo", minutes, minutes);
        }
        if (delta.TotalHours < 24)
        {
            var hours = (int)delta.TotalHours;
            return Plural.Get("Common_RelativeTime_HoursAgo", hours, hours);
        }
        if (delta.TotalDays < 7)
        {
            var days = (int)delta.TotalDays;
            return Plural.Get("Common_RelativeTime_DaysAgo", days, days);
        }
        return utc.ToLocalTime().ToString("dd MMM yyyy", LocalizationService.Instance.CurrentCulture);
    }

    /// <summary>The forward-looking counterpart of <see cref="Format"/> — "in 2 minutes" instead of "2
    /// minutes ago" — used by the Polar sync backoff countdown (<c>PolarSyncService.NextSyncAttemptUtc</c>).
    /// Minute-granularity only since that's as precise as the backoff interval itself ever gets.</summary>
    public static string FormatUntil(DateTime utc, DateTime nowUtc)
    {
        var delta = utc - nowUtc;
        if (delta.TotalMinutes < 1) return LocalizationService.Instance["Common_RelativeTime_Soon"];

        var minutes = (int)Math.Ceiling(delta.TotalMinutes);
        return Plural.Get("Common_RelativeTime_InMinutes", minutes, minutes);
    }

    /// <summary>"dd MMM yyyy, HH:mm" in the app's current locale — the full absolute timestamp shown next to
    /// a notification or a reminder's creation date, instead of <see cref="Format"/>'s relative wording.
    /// Fase 6 (gap 33) consolidates this into one place: it used to be duplicated in
    /// <c>NotificationItemViewModel</c>, <c>ReminderListItemViewModel</c> and <c>EditorStatsViewModel</c>,
    /// and the latter two omitted <see cref="LocalizationService.CurrentCulture"/> entirely — a language
    /// leak where month names silently fell back to the OS/thread culture instead of the language the user
    /// picked inside Enki.</summary>
    public static string FormatAbsolute(DateTime utc) =>
        utc.ToLocalTime().ToString("dd MMM yyyy, HH:mm", LocalizationService.Instance.CurrentCulture);

    /// <summary><see cref="FormatAbsolute"/> without the year — used where the year is implied (due dates,
    /// last-synced timestamps). Same consolidation as <see cref="FormatAbsolute"/>: previously duplicated
    /// across <c>RemindersViewModel</c>, <c>SettingsViewModel</c> and <c>PolarTaskManagementViewModel</c>,
    /// two of which never passed the current culture through.</summary>
    public static string FormatAbsoluteShort(DateTime utc) =>
        utc.ToLocalTime().ToString("dd MMM, HH:mm", LocalizationService.Instance.CurrentCulture);
}
