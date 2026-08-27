using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    private (SessionsViewModel Vm, SessionService Sessions) CreateViewModel(Action<Session>? onSessionLocked = null)
    {
        var settings = new SettingsService(_storageDirectory);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        var notifications = new NotificationService(settings, _storageDirectory);
        var vm = new SessionsViewModel(vault, sessions, notifications, (_, _) => { }, () => { },
            onSessionLocked: onSessionLocked);
        return (vm, sessions);
    }

    [Fact]
    public void CanConfirmCreate_WhenProtected_RequiresMatchingPasswordsOfMinimumLength()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;

        vm.NewSessionPassword = "abc";
        vm.NewSessionPasswordConfirm = "abc";
        Assert.False(vm.CanConfirmCreate); // 3 caracteres: por debajo del mínimo

        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "otra-distinta-larga";
        Assert.False(vm.CanConfirmCreate); // no coinciden

        vm.NewSessionPasswordConfirm = "una-contraseña-larga";
        Assert.True(vm.CanConfirmCreate);
    }

    [Fact]
    public void CanConfirmCreate_AcceptsAFourCharacterPassword()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;

        vm.NewSessionPassword = "1234";
        vm.NewSessionPasswordConfirm = "1234";

        Assert.True(vm.CanConfirmCreate);
    }

    [Theory]
    [InlineData("1234")]              // solo dígitos
    [InlineData("abcd")]              // solo letras minúsculas
    [InlineData("ABCD")]              // solo mayúsculas
    [InlineData("....")]              // solo símbolos
    [InlineData("    ")]              // solo espacios
    [InlineData("ñáé€")]              // no ASCII
    [InlineData("🔒🔑🗝️📓")]           // emoji
    public void CanConfirmCreate_ImposesNoCompositionRules(string password)
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;

        vm.NewSessionPassword = password;
        vm.NewSessionPasswordConfirm = password;

        Assert.True(vm.CanConfirmCreate);
    }

    [Fact]
    public async Task AFourCharacterPassword_RoundTripsThroughLockAndUnlock()
    {
        var (vm, sessions) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "1234";
        vm.NewSessionPasswordConfirm = "1234";
        await vm.ConfirmCreateSessionCommand.ExecuteAsync(null);

        var created = sessions.List().Single(s => s.Name == "Privada");
        sessions.Lock(created);
        Assert.False(sessions.IsUnlocked(created));

        Assert.False(sessions.Unlock(created, "12345"));
        Assert.True(sessions.Unlock(created, "1234"));
    }

    [Fact]
    public async Task ConfirmCreateSession_Protected_OpensRecoveryCodesDialogWith5Codes()
    {
        var (vm, sessions) = CreateViewModel();
        vm.NewSessionName = "Diario privado";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "una-contraseña-larga";

        await vm.ConfirmCreateSessionCommand.ExecuteAsync(null);

        Assert.True(vm.IsRecoveryCodesDialogOpen);
        Assert.Equal(5, vm.PendingRecoveryCodes.Count);
        Assert.False(vm.IsCreateDialogOpen);

        var created = sessions.List().Single();
        Assert.True(created.IsEncrypted);
        Assert.True(sessions.IsUnlocked(created));
    }

    [Fact]
    public async Task ConfirmCreateSession_NotProtected_NeverOpensRecoveryCodesDialog()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Sesión normal";

        await vm.ConfirmCreateSessionCommand.ExecuteAsync(null);

        Assert.False(vm.IsRecoveryCodesDialogOpen);
        Assert.Empty(vm.PendingRecoveryCodes);
    }

    [Fact]
    public async Task CloseRecoveryCodesDialog_ClearsCodesFromMemory()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Diario privado";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "una-contraseña-larga";
        vm.NewSessionPasswordConfirm = "una-contraseña-larga";
        await vm.ConfirmCreateSessionCommand.ExecuteAsync(null);

        vm.CloseRecoveryCodesDialogCommand.Execute(null);

        Assert.False(vm.IsRecoveryCodesDialogOpen);
        Assert.Empty(vm.PendingRecoveryCodes);
    }

    [Fact]
    public async Task ConfirmUnlock_WrongPassword_SetsError_DoesNotNavigate()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.UnlockPasswordInput = "incorrecta";
        await vm.ConfirmUnlockCommand.ExecuteAsync(null);

        Assert.NotNull(vm.SessionPendingUnlock);
        Assert.NotNull(vm.UnlockError);
        Assert.False(item.IsUnlocked);
    }

    [Fact]
    public async Task ConfirmUnlock_CorrectPassword_ClosesDialog_MarksItemUnlocked()
    {
        var (vm, sessions) = CreateViewModel();
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        sessions.Lock(session);
        var item = new SessionListItemViewModel(session) { IsUnlocked = false };

        vm.RequestUnlockSessionCommand.Execute(item);
        vm.UnlockPasswordInput = "correcta-123";
        await vm.ConfirmUnlockCommand.ExecuteAsync(null);

        Assert.Null(vm.SessionPendingUnlock);
        Assert.True(item.IsUnlocked);
        Assert.True(sessions.IsUnlocked(session));
    }

    [Fact]
    public async Task ConfirmPasswordReset_ValidCode_UnlocksWithNewPassword()
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
        await vm.ConfirmPasswordResetCommand.ExecuteAsync(null);

        Assert.Null(vm.SessionPendingUnlock);
        Assert.True(item.IsUnlocked);

        sessions.Lock(session);
        Assert.False(sessions.Unlock(session, "vieja-123"));
        Assert.True(sessions.Unlock(session, "nueva-contraseña-larga"));
    }

    [Fact]
    public async Task ConfirmPasswordReset_InvalidCode_SetsError_StaysLocked()
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
        await vm.ConfirmPasswordResetCommand.ExecuteAsync(null);

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

    /// <summary>Covers Fase 1 (docs/version-alfa-3/02-roadmap-implementacion.md): the onSessionLocked
    /// callback (wired to <c>NotesViewModel.CloseEditorsForLockedSession</c> in the real shell) must fire
    /// while the session's DEK is still unlocked, not after — otherwise it couldn't flush a dirty open tab's
    /// pending save, which needs to encrypt+write before the key disappears.</summary>
    [Fact]
    public void LockSession_InvokesOnSessionLockedCallback_WhileStillUnlocked()
    {
        Session? notifiedSession = null;
        var wasUnlockedDuringCallback = false;
        SessionService? sessionsRef = null;
        var (vm, sessions) = CreateViewModel(onSessionLocked: s =>
        {
            notifiedSession = s;
            wasUnlockedDuringCallback = sessionsRef!.IsUnlocked(s);
        });
        sessionsRef = sessions;
        var (session, _) = sessions.CreateEncrypted("Diario privado", "correcta-123");
        var item = new SessionListItemViewModel(session) { IsUnlocked = true };

        vm.LockSessionCommand.Execute(item);

        Assert.Same(session, notifiedSession);
        Assert.True(wasUnlockedDuringCallback);
        Assert.False(sessions.IsUnlocked(session)); // locked for real once the callback returns
    }
}
