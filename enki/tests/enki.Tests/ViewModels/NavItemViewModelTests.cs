using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers the Fase 2 nav badge (docs/analisis-mejoras-funcionalidades.md §4.9: "badges de conteo
/// en Papelera y Calendario") — <see cref="NavItemViewModel.HasCount"/> is the derived flag the sidebar
/// binds to hide the badge when nothing is pending.</summary>
public class NavItemViewModelTests
{
    [Fact]
    public void HasCount_IsFalse_WhenCountIsZero()
    {
        var item = new NavItemViewModel { Label = "Papelera", Icon = Material.Icons.MaterialIconKind.TrashCanOutline, Page = AppPage.Trash };
        Assert.False(item.HasCount);
    }

    [Fact]
    public void HasCount_BecomesTrue_WhenCountIsSet()
    {
        var item = new NavItemViewModel { Label = "Papelera", Icon = Material.Icons.MaterialIconKind.TrashCanOutline, Page = AppPage.Trash };

        item.Count = 3;

        Assert.True(item.HasCount);
    }

    [Fact]
    public void HasCount_RaisesPropertyChanged_WhenCountChanges()
    {
        var item = new NavItemViewModel { Label = "Papelera", Icon = Material.Icons.MaterialIconKind.TrashCanOutline, Page = AppPage.Trash };
        var raised = false;
        item.PropertyChanged += (_, e) => raised |= e.PropertyName == nameof(NavItemViewModel.HasCount);

        item.Count = 1;

        Assert.True(raised);
    }

    [Fact]
    public void IsDanger_IsTrue_ForTrashPage()
    {
        var item = new NavItemViewModel { Label = "Papelera", Icon = Material.Icons.MaterialIconKind.TrashCanOutline, Page = AppPage.Trash };
        Assert.True(item.IsDanger);
    }

    [Fact]
    public void IsDanger_IsFalse_ForNonTrashPage()
    {
        var item = new NavItemViewModel { Label = "Dashboard", Icon = Material.Icons.MaterialIconKind.ViewDashboardOutline, Page = AppPage.Dashboard };
        Assert.False(item.IsDanger);
    }
}
