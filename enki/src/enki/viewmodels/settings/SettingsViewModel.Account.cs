using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Ajustes › Cuenta: vínculo/desvínculo con Polar (docs/supabase-sync/08) y el estado de
/// sincronización en vivo (última sincronización, errores, próximo reintento, botón "Sincronizar ahora") —
/// merges what used to be two separate regions ("Privacidad › vínculo con Polar" and "Cuenta ›
/// sincronización con Polar", the same page) into one file, split out of <c>SettingsViewModel.cs</c>. Still
/// the very same <see cref="SettingsViewModel"/> class — partial types share every field/property
/// regardless of which file declares them — so no behavior change.</summary>
public sealed partial class SettingsViewModel
{
    // ===================== Privacidad › vínculo con Polar =====================

    public bool IsSupabaseConfigured => _polarAuthService.IsConfigured;

    [ObservableProperty]
    private bool _isPolarLinked;

    [ObservableProperty]
    private string? _polarLinkedEmail;

    /// <summary>"Última sincronización" en Ajustes › Cuenta (docs/ajustes-docs/03-cuenta-y-sincronizacion-polar.md) —
    /// el dato ya existía en <see cref="PolarSyncService.LastSyncedUtc"/>, solo faltaba mostrarlo aquí.
    /// Mismo formato ("dd MMM, HH:mm" / "Nunca") que <c>PolarTaskManagementViewModel.RefreshSyncStatus</c>,
    /// refrescado al construir y cada vez que <see cref="PolarSyncService.SyncCompleted"/> se dispara.</summary>
    [ObservableProperty]
    private string _lastSyncedLabel = LocalizationService.Instance["Common_Never"];

    [ObservableProperty]
    private bool _isSyncingNow;

    [ObservableProperty]
    private string? _syncError;

    /// <summary>Composed "Couldn't sync: {0}" label — same reasoning as the equivalent properties in
    /// <c>RemindersViewModel</c>/<c>PolarTaskManagementViewModel</c>: kept out of XAML so the word order can
    /// be adapted per locale instead of being fixed by a <c>StringFormat</c>. Ajustes › Cuenta previously
    /// showed no sync error at all (10.2 §8) — only "Last synced", with no way to tell a stale timestamp
    /// apart from a genuinely broken sync.</summary>
    public string? SyncErrorLabel => SyncError is { } error
        ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Settings_Account_SyncErrorLabel"], error)
        : null;

    partial void OnSyncErrorChanged(string? value) => OnPropertyChanged(nameof(SyncErrorLabel));

    /// <summary>Non-null only while <see cref="PolarSyncService.Status"/> is
    /// <see cref="PolarSyncStatus.Offline"/> and a retry is actually scheduled — "Retrying in 2 minutes".</summary>
    [ObservableProperty]
    private string? _nextRetryLabel;

    /// <summary>True right after this view's own "Sync now" click was discarded because another sync (the
    /// background timer, the realtime debounce, or another view's own manual sync) already held the gate —
    /// distinct from a completed cycle that simply changed nothing (Fase 9, docs/analisis-proyecto/
    /// roadmap.md). Cleared the next time any sync cycle actually completes.</summary>
    [ObservableProperty]
    private bool _lastSyncNowWasSkipped;

    public string SyncNowButtonLabel => IsSyncingNow
        ? LocalizationService.Instance["Settings_Account_SyncingButton"]
        : LocalizationService.Instance["Settings_Account_SyncNowButton"];

    partial void OnIsSyncingNowChanged(bool value) => OnPropertyChanged(nameof(SyncNowButtonLabel));

    [ObservableProperty]
    private string _polarEmailInput = string.Empty;

    [ObservableProperty]
    private string _polarPasswordInput = string.Empty;

    [ObservableProperty]
    private bool _isLinkingPolar;

    [ObservableProperty]
    private string? _polarLinkError;

    public string LinkButtonLabel => IsLinkingPolar
        ? LocalizationService.Instance["Settings_Account_LinkingButton"]
        : LocalizationService.Instance["Settings_Account_LinkPolar_Title"];

    partial void OnIsLinkingPolarChanged(bool value) => OnPropertyChanged(nameof(LinkButtonLabel));

    /// <summary>Signs in with an email/password that already exists in Supabase (docs/supabase-sync/08) —
    /// Enki never creates accounts, only links to one that's already set up. Success unlocks "Gestión de
    /// tareas" in Kanban and starts background sync (both wired off <see cref="PolarAuthService.LinkStateChanged"/>
    /// by <c>MainWindowViewModel</c>/<c>KanbanViewModel</c>).</summary>
    [RelayCommand]
    private async Task LinkPolar()
    {
        if (IsLinkingPolar) return;

        IsLinkingPolar = true;
        PolarLinkError = null;

        var (success, error) = await _polarAuthService.LinkAsync(PolarEmailInput, PolarPasswordInput);

        IsLinkingPolar = false;
        if (success)
        {
            PolarEmailInput = string.Empty;
            PolarPasswordInput = string.Empty;
        }
        else
        {
            PolarLinkError = error;
        }
    }

    [RelayCommand]
    private void UnlinkPolar()
    {
        try { _polarAuthService.Unlink(); }
        catch (System.IO.IOException)
        {
            PolarLinkError = LocalizationService.Instance["Polar_Auth_Error_SecureStorage"];
            return;
        }
        PolarEmailInput = string.Empty;
        PolarPasswordInput = string.Empty;
        PolarLinkError = null;
    }

    // ===================== Cuenta › sincronización con Polar (Ajustes › Cuenta) =====================
    // docs/ajustes-docs/03-cuenta-y-sincronizacion-polar.md — el dato y el método ya existían en
    // PolarSyncService, solo faltaba exponerlos aquí.

    private void RefreshLastSyncedLabel()
    {
        LastSyncedLabel = _polarSyncService.LastSyncedUtc is { } utc
            ? RelativeTimeFormatter.FormatAbsoluteShort(utc)
            : LocalizationService.Instance["Common_Never"];
        SyncError = _polarSyncService.LastError;
        NextRetryLabel = _polarSyncService.Status == PolarSyncStatus.Offline
            && _polarSyncService.NextSyncAttemptUtc is { } next
                ? string.Format(CultureInfo.CurrentCulture, LocalizationService.Instance["Polar_Sync_NextRetryLabel"], RelativeTimeFormatter.FormatUntil(next, DateTime.UtcNow))
                : null;
    }

    [RelayCommand]
    private async Task SyncNow()
    {
        if (IsSyncingNow) return;

        IsSyncingNow = true;
        var outcome = await _polarSyncService.SyncNowAsync();
        IsSyncingNow = false;
        LastSyncNowWasSkipped = outcome == PolarSyncOutcome.SkippedAlreadyRunning;
        // RefreshLastSyncedLabel ya se dispara desde la suscripción a SyncCompleted del constructor.
    }
}
