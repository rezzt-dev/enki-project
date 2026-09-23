using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Cubre Ajustes › Editor › Plantillas: que el desplegable de la nota diaria se pueble con las
/// plantillas del vault, que la elección se guarde en <see cref="Models.VaultSettings.DailyNoteTemplate"/>
/// y que una plantilla desaparecida no arrastre consigo el ajuste guardado
/// (.docs/project-info/plantillas-de-notas.md §5).</summary>
public class SettingsViewModelTemplatesTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-" + Guid.NewGuid().ToString("N"));

    private readonly string _vaultPath;

    public SettingsViewModelTemplatesTests() => _vaultPath = Path.Combine(_storageDirectory, "vault");

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private void WriteTemplate(string relativePath, string content)
    {
        var path = Path.Combine(_vaultPath, ".enki", "templates", relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private (SettingsViewModel Settings, VaultSettingsService VaultSettings, NoteTemplateService Templates) CreateViewModel()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettingsService = new VaultSettingsService(vault);
        var authService = new PolarAuthService(settingsService);
        var backupService = new VaultBackupService(vault, _storageDirectory);
        var syncService = new PolarSyncService(authService, new SupabaseRestClient(authService),
            new PolarTaskService(_storageDirectory), new ReminderService(_storageDirectory), settingsService);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var templates = new NoteTemplateService(vault);

        var vm = new SettingsViewModel(
            settingsService, new CustomThemeService(settingsService), new BrandLogoService(_storageDirectory),
            vaultSettingsService, vault, backupService, authService, syncService, notificationService,
            switchVault: (_, _) => Task.FromResult(true), onTitleBarModeChanged: _ => { }, onNotificationsPanelChanged: _ => { },
            onRemindersPanelChanged: _ => { }, onCalendarShowSidebarScrollbarChanged: _ => { },
            onSidebarSectionChanged: (_, _) => { }, onSidebarSectionOrderChanged: () => { },
            onCalendarSettingsChanged: () => { }, onDashboardShowScrollbarChanged: _ => { },
            onKanbanBoardOrientationChanged: _ => { }, onPolarShowTaskTagsChanged: _ => { },
            onUiZoomChanged: _ => { }, onLanguageChanged: () => { }, noteTemplateService: templates);

        return (vm, vaultSettingsService, templates);
    }

    /// <summary>La primera opción es siempre "sin plantilla", para que el comportamiento de salida de la
    /// nota diaria se pueda recuperar sin borrar nada.</summary>
    [Fact]
    public void DailyNoteTemplateOptions_AlwaysOfferTheNoTemplateChoiceFirst()
    {
        var (vm, _, _) = CreateViewModel();

        var first = Assert.Single(vm.DailyNoteTemplateOptions);
        Assert.Null(first.RelativePath);
        Assert.Same(first, vm.SelectedDailyNoteTemplate);
    }

    [Fact]
    public void DailyNoteTemplateOptions_ListTheTemplatesOfTheVault()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        WriteTemplate("trabajo/reunion.md", "---\nname: Reunión\n---\nCuerpo");
        var (vm, _, _) = CreateViewModel();

        var labels = vm.DailyNoteTemplateOptions.Select(o => o.Label).ToArray();

        Assert.Equal(3, labels.Length);
        Assert.Contains("Diario", labels);
        // Una plantilla en subcarpeta muestra su ruta, que es lo que la distingue de otra homónima.
        Assert.Contains("Reunión (trabajo/reunion.md)", labels);
    }

    [Fact]
    public void SelectingATemplate_PersistsItInTheVaultSettings()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var (vm, vaultSettings, _) = CreateViewModel();

        vm.SelectedDailyNoteTemplate = vm.DailyNoteTemplateOptions.Single(o => o.RelativePath == "diario.md");

        Assert.Equal("diario.md", vaultSettings.Settings.DailyNoteTemplate);
        // Y sobrevive a releer el archivo del vault, no solo en memoria.
        var reloaded = new VaultSettingsService(new VaultService(new SettingsService(_storageDirectory)));
        Assert.Equal("diario.md", reloaded.Settings.DailyNoteTemplate);
    }

    [Fact]
    public void SelectingNoTemplate_ClearsTheSetting()
    {
        WriteTemplate("diario.md", "---\nname: Diario\n---\nCuerpo");
        var (vm, vaultSettings, _) = CreateViewModel();
        vm.SelectedDailyNoteTemplate = vm.DailyNoteTemplateOptions.Single(o => o.RelativePath == "diario.md");

        vm.SelectedDailyNoteTemplate = vm.DailyNoteTemplateOptions[0];

        Assert.Null(vaultSettings.Settings.DailyNoteTemplate);
    }

    /// <summary>Crear una plantilla desde Documentos refresca este desplegable por
    /// <see cref="NoteTemplateService.TemplatesChanged"/>, sin que Ajustes tenga que preguntar.</summary>
    [Fact]
    public void CreatingATemplate_RefreshesTheDropdown()
    {
        var (vm, _, templates) = CreateViewModel();

        templates.CreateTemplate("Nueva");

        Assert.Contains(vm.DailyNoteTemplateOptions, o => o.RelativePath == "Nueva.md");
    }

    /// <summary>Si la plantilla configurada ya no existe, la selección cae en "sin plantilla" pero el
    /// ajuste guardado no se toca: recuperar el archivo devuelve la configuración a donde estaba.</summary>
    [Fact]
    public void AMissingConfiguredTemplate_FallsBackWithoutClearingTheStoredSetting()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var vault = new VaultService(settingsService);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        var vaultSettings = new VaultSettingsService(vault);
        vaultSettings.Settings.DailyNoteTemplate = "borrada.md";
        vaultSettings.Save();

        var (vm, reloadedSettings, _) = CreateViewModel();

        Assert.Null(vm.SelectedDailyNoteTemplate?.RelativePath);
        Assert.Equal("borrada.md", reloadedSettings.Settings.DailyNoteTemplate);
    }
}
