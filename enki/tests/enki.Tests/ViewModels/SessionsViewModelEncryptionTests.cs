using System;
using System.IO;
using System.Linq;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the password-protected-session UI flow added to <see cref="SessionsViewModel"/>: the
/// create-dialog's protect toggle, the post-creation recovery-codes screen, and the unlock/forgot-password
/// dialog. Same construction pattern as <see cref="SessionsViewModelTests"/>. Doesn't exercise
/// <c>DownloadRecoveryCodesCommand</c>/<c>CopyRecoveryCodesToClipboardCommand</c> — both depend on a real
/// Avalonia <c>TopLevel</c> (StorageProvider/Clipboard), which isn't available in a headless xUnit run;
/// they degrade to a safe no-op here since <c>App.MainWindowRef</c> is null outside the running app.</summary>
public class SessionsViewModelEncryptionTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SessionsViewModelEncryptionTests()
    {
        _vaultPath = Path.Combine(_storageDirectory, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private (SessionsViewModel Vm, SessionService Sessions) CreateViewModel()
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { });
        return (vm, sessions);
    }

    [Fact]
    public void CanConfirmCreate_WhenProtected_RequiresMatchingPasswordsOfMinimumLength()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;

        vm.NewSessionPassword = "short";
        vm.NewSessionPasswordConfirm = "short";
        Assert.False(vm.CanConfirmCreate); // too short

        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "otra-distinta-larga";
        Assert.False(vm.CanConfirmCreate); // mismatch

        vm.NewSessionPasswordConfirm = "una-contraseña-larga";
        Assert.True(vm.CanConfirmCreate);
    }

    [Fact]
    public void ConfirmCreateSession_Protected_OpensRecoveryCodesDialogWith5Codes()
    {
        var (vm, sessions) = CreateViewModel();
        vm.NewSessionName = "Diario privado";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "una-contraseña-larga";

        vm.ConfirmCreateSessionCommand.Execute(null);

        Assert.True(vm.IsRecoveryCodesDialogOpen);
        Assert.Equal(5, vm.PendingRecoveryCodes.Count);
        Assert.False(vm.IsCreateDialogOpen);

        var created = sessions.List().Single();
        Assert.True(created.IsEncrypted);
        Assert.True(sessions.IsUnlocked(created));
    }

    [Fact]
    public void ConfirmCreateSession_NotProtected_NeverOpensRecoveryCodesDialog()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Sesión normal";

        vm.ConfirmCreateSessionCommand.Execute(null);

        Assert.False(vm.IsRecoveryCodesDialogOpen);
        Assert.Empty(vm.PendingRecoveryCodes);
    }

    [Fact]
    public void CloseRecoveryCodesDialog_ClearsCodesFromMemory()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Diario privado";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "una-contraseña-larga";
        vm.ConfirmCreateSessionCommand.Execute(null);

        vm.CloseRecoveryCodesDialogCommand.Execute(null);

        Assert.False(vm.IsRecoveryCodesDialogOpen);
        Assert.Empty(vm.PendingRecoveryCodes);
    }

    [Fact]
    public void ConfirmUnlock_WrongPassword_SetsError_DoesNotNavigate()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.UnlockPasswordInput = "incorrecta";
        vm.ConfirmUnlockCommand.Execute(null);

        Assert.NotNull(vm.SessionPendingUnlock);
        Assert.NotNull(vm.UnlockError);
        Assert.False(item.IsUnlocked);
    }

    [Fact]
    public void ConfirmUnlock_CorrectPassword_ClosesDialog_MarksItemUnlocked()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.UnlockPasswordInput = "correcta-123";
        vm.ConfirmUnlockCommand.Execute(null);

        Assert.Null(vm.SessionPendingUnlock);
        Assert.True(item.IsUnlocked);
        Assert.True(sessions.IsUnlocked(session));
    }

    [Fact]
    public void ConfirmPasswordReset_ValidCode_UnlocksWithNewPassword()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, codes) = sessions.CreateEncrypted("Diario privado", "vieja-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.BeginForgotPasswordCommand.Execute(null);
        vm.RecoveryCodeInput = codes[1];
        vm.NewPasswordInput = "nueva-contraseña-larga";
        vm.NewPasswordConfirmInput = "nueva-contraseña-larga";
        vm.ConfirmPasswordResetCommand.Execute(null);

        Assert.Null(vm.SessionPendingUnlock);
        Assert.True(item.IsUnlocked);

        sessions.Lock(session);
        Assert.False(sessions.Unlock(session, "vieja-123"));
        Assert.True(sessions.Unlock(session, "nueva-contraseña-larga"));
    }

    [Fact]
    public void ConfirmPasswordReset_InvalidCode_SetsError_StaysLocked()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "vieja-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.BeginForgotPasswordCommand.Execute(null);
        vm.RecoveryCodeInput = "CODIGOQUENOEXISTEXXXX";
        vm.NewPasswordInput = "nueva-contraseña-larga";
        vm.NewPasswordConfirmInput = "nueva-contraseña-larga";
        vm.ConfirmPasswordResetCommand.Execute(null);

        Assert.NotNull(vm.UnlockError);
        Assert.False(item.IsUnlocked);
        Assert.False(sessions.IsUnlocked(session));
    }

    [Fact]
    public void LockSession_LocksAndUpdatesItemState()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        var item = new SessionListItemViewModel(session) { IsUnlocked = true };

        vm.LockSessionCommand.Execute(item);

        Assert.False(item.IsUnlocked);
        Assert.False(sessions.IsUnlocked(session));
    }
}
