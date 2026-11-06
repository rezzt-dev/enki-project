using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Botón global de rotación del grafo de esferas y clic sobre una esfera: estado inicial, elección
/// explícita persistida con <see cref="SettingsService"/>, preferencia de movimiento reducido del sistema y
/// selección de la sesión. Mismo patrón de directorio temporal por test que el resto de pruebas de Sesiones.</summary>
public class SessionsViewModelGraphRotationTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private async Task<SessionsViewModel> CreateViewModelAsync(SettingsService settings)
    {
        var vault = new VaultService(settings);
        vault.InitializeVault(Path.Combine(_storageDirectory, "vault"), seedWelcomeNote: false);
        var sessions = new SessionService(vault);
        sessions.Create("Investigación");
        var vm = new SessionsViewModel(vault, sessions, new NotificationService(settings, _storageDirectory),
            (_, _) => { }, () => { }, settingsService: settings);
        await vm.PendingLoad;
        return vm;
    }

    [Fact]
    public async Task Rotation_IsOnByDefault_AndTheButtonChoiceIsRememberedAcrossRuns()
    {
        var settings = new SettingsService(_storageDirectory);
        var vm = await CreateViewModelAsync(settings);
        Assert.True(vm.IsGraphRotationEnabled);
        Assert.False(vm.HasExplicitGraphRotationPreference);
        var raised = 0;
        vm.GraphRotationChanged += () => raised++;
        var tooltipBefore = vm.GraphRotationTooltip;

        vm.ToggleGraphRotationCommand.Execute(null);

        Assert.False(vm.IsGraphRotationEnabled);
        Assert.Equal(1, raised);
        Assert.NotEqual(tooltipBefore, vm.GraphRotationTooltip);
        Assert.True(vm.HasExplicitGraphRotationPreference);
        var reloaded = new SettingsService(_storageDirectory);
        Assert.False(reloaded.Settings.SessionsGraphRotationEnabled);
        Assert.False((await CreateViewModelAsync(reloaded)).IsGraphRotationEnabled);
    }

    [Fact]
    public async Task SubtleMotionProfile_StartsStill_UntilTheUserChooses()
    {
        var settings = new SettingsService(_storageDirectory);
        settings.Settings.UseSubtleAnimations = true;
        settings.Save();
        var vm = await CreateViewModelAsync(settings);
        Assert.False(vm.IsGraphRotationEnabled);

        vm.ToggleGraphRotationCommand.Execute(null);
        Assert.True(vm.IsGraphRotationEnabled);
        Assert.True(new SettingsService(_storageDirectory).Settings.SessionsGraphRotationEnabled);
    }

    [Fact]
    public async Task SystemReducedMotion_StopsRotationOnlyWithoutAnExplicitChoice_AndIsNotSaved()
    {
        var settings = new SettingsService(_storageDirectory);
        var vm = await CreateViewModelAsync(settings);
        vm.ApplySystemReducedMotion();
        Assert.False(vm.IsGraphRotationEnabled);
        Assert.Null(new SettingsService(_storageDirectory).Settings.SessionsGraphRotationEnabled);

        vm.ToggleGraphRotationCommand.Execute(null);
        vm.ApplySystemReducedMotion();
        Assert.True(vm.IsGraphRotationEnabled);
    }

    [Fact]
    public async Task ClickingASphere_SelectsItsSession_AndIgnoresUnknownGroups()
    {
        var vm = await CreateViewModelAsync(new SettingsService(_storageDirectory));
        var target = vm.Sessions.Single(s => s.Name == "Investigación");

        vm.SelectGraphSession(target.Session.Id);
        Assert.Same(target, vm.SelectedSession);

        vm.SelectGraphSession(enki.Models.SessionGraph.RootGroupId);
        vm.SelectGraphSession("missing");
        Assert.Same(target, vm.SelectedSession);
    }
}
