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

    /// <summary>El mínimo subió de 4 a 12 (.docs/auditoria-seguridad-y-fallos-criticos.md, SEC-02): el
    /// atacante de una sesión protegida no pelea con la app sino con una copia del manifiesto, offline y
    /// sin límite de intentos, y 4 caracteres se agotan en minutos por mucho PBKDF2 que haya detrás. Once
    /// caracteres tienen que seguir siendo rechazados aunque "parezcan" una contraseña.</summary>
    [Theory]
    [InlineData("1234")]
    [InlineData("abcdefghijk")]       // 11: justo por debajo del mínimo
    public void CanConfirmCreate_RejectsAPasswordBelowTheMinimum(string password)
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;

        vm.NewSessionPassword = password;
        vm.NewSessionPasswordConfirm = password;

        Assert.False(vm.CanConfirmCreate);
        Assert.True(vm.IsNewSessionPasswordTooShort);
    }

    /// <summary>El aviso de "demasiado corta" solo aparece cuando hay algo escrito: con el campo vacío
    /// todavía no hay nada que corregir, y con una contraseña válida tampoco.</summary>
    [Fact]
    public void IsNewSessionPasswordTooShort_OnlyWhileThereIsSomethingToFix()
    {
        var (vm, _) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;
        Assert.False(vm.IsNewSessionPasswordTooShort);

        vm.NewSessionPassword = "corta";
        Assert.True(vm.IsNewSessionPasswordTooShort);

        vm.NewSessionPassword = "una-contraseña-larga";
        Assert.False(vm.IsNewSessionPasswordTooShort);

        // Desactivar la protección deja de exigir contraseña, así que el aviso también se retira.
        vm.NewSessionPassword = "corta";
        vm.NewSessionIsProtected = false;
        Assert.False(vm.IsNewSessionPasswordTooShort);
    }

    /// <summary>La longitud es el único requisito: sigue sin haber reglas de composición, porque la
    /// contraseña acaba en PBKDF2 sobre sus bytes UTF-8 y ahí toda combinación vale igual.</summary>
    [Theory]
    [InlineData("123456789012")]              // solo dígitos
    [InlineData("abcdefghijkl")]              // solo letras minúsculas
    [InlineData("ABCDEFGHIJKL")]              // solo mayúsculas
    [InlineData("............")]              // solo símbolos
    [InlineData("            ")]              // solo espacios
    [InlineData("ñáéíóúñáéíóú")]              // no ASCII
    [InlineData("🔒🔑🗝️📓🔒🔑🗝️📓🔒🔑🗝️📓")]     // emoji
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
    public async Task APassphrase_RoundTripsThroughLockAndUnlock()
    {
        var (vm, sessions) = CreateViewModel();
        vm.NewSessionName = "Privada";
        vm.NewSessionIsProtected = true;
        vm.NewSessionPassword = "caballo-batería-grapa";
        vm.NewSessionPasswordConfirm = "caballo-batería-grapa";
        await vm.ConfirmCreateSessionCommand.ExecuteAsync(null);

        var created = sessions.List().Single(s => s.Name == "Privada");
        sessions.Lock(created);
        Assert.False(sessions.IsUnlocked(created));

        Assert.False(sessions.Unlock(created, "caballo-batería-grapas"));
        Assert.True(sessions.Unlock(created, "caballo-batería-grapa"));
    }

    /// <summary>El mínimo nuevo solo se aplica al elegir contraseña. Una sesión creada por una versión
    /// anterior con una contraseña de 4 caracteres tiene que seguir abriéndose: bloquear su acceso sería
    /// convertir un endurecimiento en pérdida de datos.</summary>
    [Fact]
    public void AnExistingShortPassword_StillUnlocks()
    {
        var (_, sessions) = CreateViewModel();
        var (created, _) = sessions.CreateEncrypted("Heredada", "1234");

        sessions.Lock(created);
        Assert.False(sessions.IsUnlocked(created));
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
