using System;
using System.IO;
using enki.Localization;
using enki.Models;

namespace enki.Services;

/// <summary>Maps an I/O failure raised by a <see cref="VaultService"/> write operation to a translated,
/// user-facing reason — shared by every view model that surfaces such a failure via
/// <see cref="NotificationService"/> (<c>EditorViewModel</c>, <c>NotesViewModel</c>), so the handful of
/// exception types Enki distinguishes (vault unavailable, permission denied, path not found, session
/// locked, generic I/O) are described identically everywhere instead of drifting between call sites
/// (docs/analisis-proyecto/roadmap.md, Fase 1; docs/version-alfa-3/01-analisis-detallado.md §4.1).</summary>
public static class VaultFailureReasons
{
    public static string Describe(Exception ex) => ex switch
    {
        VaultUnavailableException => LocalizationService.Instance["Notification_VaultUnavailable_Reason"],
        SessionLockedException => LocalizationService.Instance["Notification_SessionLocked_Reason"],
        UnauthorizedAccessException => LocalizationService.Instance["Common_Error_AccessDenied"],
        DirectoryNotFoundException => LocalizationService.Instance["Notification_SaveFailed_Reason_PathNotFound"],
        _ => LocalizationService.Instance["Common_Error_IOFailure"]
    };
}
