using System;
using System.IO;
using enki.Localization;

namespace enki.Services;

/// <summary>Maps an I/O failure raised by a <see cref="VaultService"/> write operation to a translated,
/// user-facing reason — shared by every view model that surfaces such a failure via
/// <see cref="NotificationService"/> (<c>EditorViewModel</c>, <c>NotesViewModel</c>), so the handful of
/// exception types Enki distinguishes (vault unavailable, permission denied, path not found, generic I/O)
/// are described identically everywhere instead of drifting between call sites
/// (docs/analisis-proyecto/roadmap.md, Fase 1).</summary>
public static class VaultFailureReasons
{
    public static string Describe(Exception ex) => ex switch
    {
        VaultUnavailableException => LocalizationService.Instance["Notification_VaultUnavailable_Reason"],
        UnauthorizedAccessException => LocalizationService.Instance["Common_Error_AccessDenied"],
        DirectoryNotFoundException => LocalizationService.Instance["Notification_SaveFailed_Reason_PathNotFound"],
        _ => LocalizationService.Instance["Common_Error_IOFailure"]
    };
}
