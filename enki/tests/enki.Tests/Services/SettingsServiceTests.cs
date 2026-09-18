using System;
using System.IO;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Covers <see cref="SettingsService"/> — the persistence every other service in the app builds
/// on (docs/analisis-proyecto/03-problemas-y-deuda-tecnica.md §8 flags it as untested despite being the
/// highest-risk shared surface). Besides plain load/save round-tripping, this covers the one genuinely
/// subtle piece of behavior here: <see cref="SettingsService.Save"/>'s cross-window merge, which only lets
/// <c>VaultPath</c> through to disk when the saving instance itself changed it, so that two Enki windows
/// sharing one settings file don't stomp each other's vault choice with an unrelated setting change.</summary>
public class SettingsServiceTests : IDisposable
{
    private readonly TestPolarCredentialStore _credentialStore = new();
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    [Fact]
    public void Save_PersistsChangesAcrossInstances()
    {
        var first = new SettingsService(_storageDirectory, _credentialStore);
        first.Settings.Theme = "Light";
        first.Settings.EditorFontSize = 18;
        first.Save();

        var second = new SettingsService(_storageDirectory, _credentialStore);

        Assert.Equal("Light", second.Settings.Theme);
        Assert.Equal(18, second.Settings.EditorFontSize);
    }

    [Fact]
    public void ParagraphJustification_OldSettingsEnableIt_AndExplicitChoicePersists()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "settings.json"), "{\"EditorWordWrap\":true}");
        var service = new SettingsService(_storageDirectory, _credentialStore);
        Assert.True(service.Settings.EditorJustifyParagraphs);
        service.Settings.EditorJustifyParagraphs = false;
        service.Save();
        Assert.False(new SettingsService(_storageDirectory, _credentialStore).Settings.EditorJustifyParagraphs);
    }

    [Fact]
    public void Load_MissingFile_FallsBackToDefaults()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);

        Assert.Null(service.Settings.VaultPath);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.False(service.LoadedFromCorruptData);
    }

    /// <summary>Fase 1 (docs/analisis-proyecto/roadmap.md): a settings.json that exists but fails to parse
    /// must be distinguishable from a brand-new install with no file at all, so the shell
    /// (<c>MainWindowViewModel</c>) can warn the user instead of silently losing every setting — including
    /// the Polar account link — the same way <c>KanbanService.LoadedFromCorruptData</c> already does.</summary>
    [Fact]
    public void Load_CorruptFile_FallsBackToDefaultsInsteadOfThrowing()
    {
        Directory.CreateDirectory(_storageDirectory);
        File.WriteAllText(Path.Combine(_storageDirectory, "settings.json"), "{ not valid json ");

        var service = new SettingsService(_storageDirectory, _credentialStore);

        Assert.Equal("Dark", service.Settings.Theme);
        Assert.True(service.LoadedFromCorruptData);
    }

    [Fact]
    public void Save_WhenThisInstanceChangedVaultPath_PersistsItsOwnValue()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);

        service.Settings.VaultPath = "/home/user/my-vault";
        service.Save();

        var reloaded = new SettingsService(_storageDirectory, _credentialStore);
        Assert.Equal("/home/user/my-vault", reloaded.Settings.VaultPath);
    }

    /// <summary>Two windows, A and B, both load the settings file with no vault configured yet. A points
    /// itself at a vault and saves. B then changes an unrelated setting (its own copy of VaultPath is
    /// still null, exactly as it was when B loaded) and saves too — B's save must not blank out the vault
    /// A just picked, since B never touched VaultPath itself.</summary>
    [Fact]
    public void Save_FromAnInstanceThatDidNotChangeVaultPath_AdoptsWhateverIsOnDiskInstead()
    {
        var windowA = new SettingsService(_storageDirectory, _credentialStore);
        var windowB = new SettingsService(_storageDirectory, _credentialStore);

        windowA.Settings.VaultPath = "/home/user/vault-a";
        windowA.Save();

        windowB.Settings.Theme = "Light";
        windowB.Save();

        Assert.Equal("/home/user/vault-a", windowB.Settings.VaultPath);

        var reloaded = new SettingsService(_storageDirectory, _credentialStore);
        Assert.Equal("/home/user/vault-a", reloaded.Settings.VaultPath);
        Assert.Equal("Light", reloaded.Settings.Theme);
    }

    [Fact]
    public void ResetToDefaults_RestoresDefaultsButKeepsVaultPath()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.VaultPath = "/home/user/my-vault";
        service.Settings.Theme = "Light";
        service.Settings.EditorFontSize = 20;
        service.Save();

        service.ResetToDefaults();

        Assert.Equal("/home/user/my-vault", service.Settings.VaultPath);
        Assert.Equal("Dark", service.Settings.Theme);
        Assert.Equal(14, service.Settings.EditorFontSize);

        var reloaded = new SettingsService(_storageDirectory, _credentialStore);
        Assert.Equal("/home/user/my-vault", reloaded.Settings.VaultPath);
        Assert.Equal("Dark", reloaded.Settings.Theme);
    }

    /// <summary>docs/agent-docs/internacionalizacion-i18n.md §2.2 — resetting settings should not force the
    /// user to re-pick a language, same reasoning as keeping <c>VaultPath</c>.</summary>
    [Fact]
    public void ResetToDefaults_KeepsLanguage()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.Language = "fr-FR";
        service.Settings.Theme = "Light";
        service.Save();

        service.ResetToDefaults();

        Assert.Equal("fr-FR", service.Settings.Language);
        Assert.Equal("Dark", service.Settings.Theme);
    }

    // ===================== BuildRedactedExportJson (Fase 4) =====================

    /// <summary>docs/analisis-proyecto/roadmap.md, Fase 4: "Exportar ajustes" must not hand out a working
    /// Polar/Supabase session to whoever ends up with the exported file.</summary>
    [Fact]
    public void BuildRedactedExportJson_OmitsPolarAccessAndRefreshTokens()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.PolarLinked = true;
        service.Settings.PolarEmail = "user@example.com";
        service.Settings.PolarAccessToken = "super-secret-access-token";
        service.Settings.PolarRefreshToken = "super-secret-refresh-token";
        service.Save();

        var json = service.BuildRedactedExportJson();

        Assert.DoesNotContain("super-secret-access-token", json);
        Assert.DoesNotContain("super-secret-refresh-token", json);
    }

    [Fact]
    public void BuildRedactedExportJson_KeepsEveryOtherSettingIntact()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.PolarAccessToken = "secret";
        service.Settings.Theme = "Dark";
        service.Settings.EditorFontSize = 18;
        service.Settings.PolarEmail = "user@example.com";
        service.Save();

        var json = service.BuildRedactedExportJson();
        var reloaded = System.Text.Json.JsonSerializer.Deserialize<enki.Models.AppSettings>(json)!;

        Assert.Equal("Dark", reloaded.Theme);
        Assert.Equal(18, reloaded.EditorFontSize);
        Assert.Equal("user@example.com", reloaded.PolarEmail);
        Assert.Null(reloaded.PolarAccessToken);
    }

    /// <summary>Building the export must not mutate the live, in-memory session the rest of the running
    /// app is still using.</summary>
    [Fact]
    public void BuildRedactedExportJson_DoesNotMutateTheLiveInMemorySettings()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.PolarAccessToken = "secret";
        service.Save();

        service.BuildRedactedExportJson();

        Assert.Equal("secret", service.Settings.PolarAccessToken);
    }

    [Fact]
    public void Save_PersistsPolarTokensOnlyInSecureStore_AndUnlinkDeletesThem()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.PolarLinked = true;
        service.Settings.PolarAccessToken = "access-secret";
        service.Settings.PolarRefreshToken = "refresh-secret";
        service.Save();

        var json = File.ReadAllText(service.SettingsFilePath);
        Assert.DoesNotContain("access-secret", json);
        Assert.DoesNotContain("refresh-secret", json);
        Assert.Equal("refresh-secret", _credentialStore.Credentials?.RefreshToken);
        Assert.Equal("access-secret", new SettingsService(_storageDirectory, _credentialStore).Settings.PolarAccessToken);

        service.Settings.PolarLinked = false;
        service.Settings.PolarAccessToken = null;
        service.Settings.PolarRefreshToken = null;
        service.Save();
        Assert.Null(_credentialStore.Credentials);
    }

    [Fact]
    public void Load_MigratesLegacyTokens_OnlyAfterSecureWriteSucceeds()
    {
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "settings.json");
        File.WriteAllText(path, "{\"PolarLinked\":true,\"PolarAccessToken\":\"old-access\",\"PolarRefreshToken\":\"old-refresh\",\"FutureSetting\":42}");

        var service = new SettingsService(_storageDirectory, _credentialStore);

        Assert.Equal("old-access", service.Settings.PolarAccessToken);
        Assert.Equal("old-refresh", _credentialStore.Credentials?.RefreshToken);
        Assert.DoesNotContain("old-access", File.ReadAllText(path));
        Assert.Contains("FutureSetting", File.ReadAllText(path));
    }

    [Fact]
    public void Load_WhenSecureStoreUnavailable_PreservesLegacyFileAndRedactsExport()
    {
        Directory.CreateDirectory(_storageDirectory);
        var path = Path.Combine(_storageDirectory, "settings.json");
        File.WriteAllText(path, "{\"PolarLinked\":true,\"PolarAccessToken\":\"old-access\",\"PolarRefreshToken\":\"old-refresh\"}");
        _credentialStore.FailReads = true;
        _credentialStore.FailWrites = true;

        var service = new SettingsService(_storageDirectory, _credentialStore);
        service.Settings.Theme = "Light";
        service.Save();

        Assert.True(service.CredentialStoreUnavailable);
        Assert.Contains("old-refresh", File.ReadAllText(path));
        Assert.DoesNotContain("old-refresh", service.BuildRedactedExportJson());
    }

    [Fact]
    public void Save_WhenSecureStoreFails_DoesNotWriteNewTokensToDisk()
    {
        var service = new SettingsService(_storageDirectory, _credentialStore);
        _credentialStore.FailWrites = true;
        service.Settings.PolarLinked = true;
        service.Settings.PolarAccessToken = "new-access";
        service.Settings.PolarRefreshToken = "new-refresh";

        Assert.Throws<IOException>(service.Save);
        Assert.False(File.Exists(service.SettingsFilePath));
    }

    [Fact]
    public void Load_WhenSecureReadFails_UnrelatedSaveDoesNotDeleteStoredSession()
    {
        var first = new SettingsService(_storageDirectory, _credentialStore);
        first.Settings.PolarLinked = true;
        first.Settings.PolarAccessToken = "access-secret";
        first.Settings.PolarRefreshToken = "refresh-secret";
        first.Save();
        _credentialStore.FailReads = true;

        var reopened = new SettingsService(_storageDirectory, _credentialStore);
        reopened.Settings.Theme = "Light";
        reopened.Save();

        Assert.True(reopened.CredentialStoreUnavailable);
        Assert.Equal("refresh-secret", _credentialStore.Credentials?.RefreshToken);
        Assert.DoesNotContain("refresh-secret", File.ReadAllText(reopened.SettingsFilePath));
    }
}
