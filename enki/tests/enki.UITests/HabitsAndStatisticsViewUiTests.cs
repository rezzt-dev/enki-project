using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using enki.Models;
using enki.Services;
using enki.ViewModels;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>Propuestas 13/15 (docs/propuesta-nuevas-funcionalidades.md): drives the real
/// <see cref="HabitsView"/>/<see cref="StatisticsView"/> XAML bound to real ViewModels — same
/// "render, don't just unit-test the ViewModel" reasoning as <see cref="KanbanViewUiTests"/>, catching
/// XAML issues (missing styles/converters, DataTemplate mismatches) compile-time XamlIL checking alone
/// might not.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public class HabitsAndStatisticsViewUiTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"enki-tests-{Guid.NewGuid():N}");
    private readonly string _vaultPath;

    public HabitsAndStatisticsViewUiTests()
    {
        HeadlessApp.EnsureInitialized();
        _vaultPath = Path.Combine(_tempDir, "vault");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    private VaultService CreateVault()
    {
        var settings = new SettingsService(_tempDir);
        var vault = new VaultService(settings);
        vault.InitializeVault(_vaultPath, seedWelcomeNote: false);
        return vault;
    }

    [Fact]
    public void HabitsView_WithOneHabit_RendersItsNameAndFrequency()
    {
        var vault = CreateVault();
        var habitService = new HabitService(vault);
        habitService.AddHabit("Meditar", HabitFrequency.Daily, []);
        var viewModel = new HabitsViewModel(habitService, new SettingsService(_tempDir));

        var view = new HabitsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        window.Show();

        var renderedTexts = view.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains("Meditar", renderedTexts);

        window.Close();
    }

    [Fact]
    public void HabitsView_EmptyState_RendersWithoutThrowing()
    {
        var habitService = new HabitService(CreateVault());
        var viewModel = new HabitsViewModel(habitService, new SettingsService(_tempDir));

        var view = new HabitsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1000, Height = 760 };
        window.Show();

        Assert.True(viewModel.IsEmpty);
        window.Close();
    }

    [Fact]
    public void StatisticsView_EmptyVault_RendersWithoutThrowing()
    {
        var vault = CreateVault();
        var settings = new SettingsService(_tempDir);
        var kanbanService = new KanbanService(_tempDir);
        var taskService = new PolarTaskService(_tempDir);
        var authService = new PolarAuthService(settings);
        var habitService = new HabitService(vault);
        var aggregationService = new StatisticsAggregationService(vault, kanbanService, taskService, authService, habitService);
        var viewModel = new StatisticsViewModel(aggregationService, authService);

        var view = new StatisticsView { DataContext = viewModel };
        var window = new Window { Content = view, Width = 1180, Height = 820 };
        window.Show();

        Assert.False(viewModel.HasEnoughData);
        window.Close();
    }
}
