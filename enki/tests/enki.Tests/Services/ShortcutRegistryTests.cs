using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using enki.Services;
using Xunit;

namespace enki.Tests.Services;

/// <summary>Propuesta 29 (docs/propuesta-nuevas-funcionalidades.md) — atajos de teclado remapeables.
/// <see cref="ShortcutRegistry"/> holds static state, so every test resets it in the constructor/Dispose to
/// avoid leaking overrides into other tests, same reasoning as the "LocalizationMutatingTests" collection
/// used elsewhere for <c>LocalizationService.Instance</c>.</summary>
public sealed class ShortcutRegistryTests : IDisposable
{
    public ShortcutRegistryTests() => ShortcutRegistry.Initialize(new Dictionary<string, string>());

    public void Dispose() => ShortcutRegistry.Initialize(new Dictionary<string, string>());

    [Fact]
    public void GetGesture_ReturnsDefault_WhenNotOverridden()
    {
        var gesture = ShortcutRegistry.GetGesture(ShortcutIds.QuickSwitcher);

        Assert.Equal(Key.P, gesture.Key);
        Assert.Equal(KeyModifiers.Control, gesture.KeyModifiers);
        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.QuickSwitcher));
    }

    [Fact]
    public void SetOverride_ChangesEffectiveGesture_MarksCustomized_AndRaisesChanged()
    {
        var raised = false;
        void Handler() => raised = true;

        ShortcutRegistry.Changed += Handler;
        try
        {
            var gesture = new KeyGesture(Key.J, KeyModifiers.Control | KeyModifiers.Alt);
            ShortcutRegistry.SetOverride(ShortcutIds.QuickSwitcher, gesture);

            Assert.True(raised);
            Assert.True(ShortcutRegistry.IsCustomized(ShortcutIds.QuickSwitcher));
            var effective = ShortcutRegistry.GetGesture(ShortcutIds.QuickSwitcher);
            Assert.Equal(Key.J, effective.Key);
            Assert.Equal(KeyModifiers.Control | KeyModifiers.Alt, effective.KeyModifiers);
        }
        finally
        {
            ShortcutRegistry.Changed -= Handler;
        }
    }

    [Fact]
    public void FindConflict_DetectsAnotherShortcutAlreadyUsingTheSameGesture()
    {
        var globalSearchGesture = ShortcutRegistry.GetGesture(ShortcutIds.GlobalSearch);

        var conflict = ShortcutRegistry.FindConflict(ShortcutIds.QuickSwitcher, globalSearchGesture);

        Assert.Equal(ShortcutIds.GlobalSearch, conflict);
    }

    [Fact]
    public void FindConflict_IgnoresTheShortcutItself()
    {
        var ownGesture = ShortcutRegistry.GetGesture(ShortcutIds.QuickSwitcher);

        Assert.Null(ShortcutRegistry.FindConflict(ShortcutIds.QuickSwitcher, ownGesture));
    }

    [Fact]
    public void FindConflict_ReturnsNull_WhenGestureIsUnique()
    {
        var uniqueGesture = new KeyGesture(Key.J, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift);

        Assert.Null(ShortcutRegistry.FindConflict(ShortcutIds.QuickSwitcher, uniqueGesture));
    }

    [Fact]
    public void ResetToDefault_RemovesOverride()
    {
        ShortcutRegistry.SetOverride(ShortcutIds.EditorBold, new KeyGesture(Key.J, KeyModifiers.Control));

        ShortcutRegistry.ResetToDefault(ShortcutIds.EditorBold);

        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.EditorBold));
        Assert.Equal(Key.B, ShortcutRegistry.GetGesture(ShortcutIds.EditorBold).Key);
    }

    [Fact]
    public void ResetAll_ClearsEveryOverride()
    {
        ShortcutRegistry.SetOverride(ShortcutIds.EditorBold, new KeyGesture(Key.J, KeyModifiers.Control));
        ShortcutRegistry.SetOverride(ShortcutIds.EditorItalic, new KeyGesture(Key.L, KeyModifiers.Control));

        ShortcutRegistry.ResetAll();

        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.EditorBold));
        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.EditorItalic));
        Assert.Empty(ShortcutRegistry.ExportOverrides());
    }

    [Fact]
    public void Initialize_SkipsMalformedGestureText_FallsBackToDefault()
    {
        ShortcutRegistry.Initialize(new Dictionary<string, string> { [ShortcutIds.EditorBold] = "not-a-gesture" });

        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.EditorBold));
        Assert.Equal(Key.B, ShortcutRegistry.GetGesture(ShortcutIds.EditorBold).Key);
    }

    [Fact]
    public void ExportOverrides_RoundTripsThroughInitialize()
    {
        ShortcutRegistry.SetOverride(ShortcutIds.TrashEmpty, new KeyGesture(Key.J, KeyModifiers.Control | KeyModifiers.Shift));
        var exported = ShortcutRegistry.ExportOverrides();

        ShortcutRegistry.Initialize(new Dictionary<string, string>());
        Assert.False(ShortcutRegistry.IsCustomized(ShortcutIds.TrashEmpty));

        ShortcutRegistry.Initialize(exported);
        Assert.True(ShortcutRegistry.IsCustomized(ShortcutIds.TrashEmpty));
        Assert.Equal(Key.J, ShortcutRegistry.GetGesture(ShortcutIds.TrashEmpty).Key);
    }

    [Fact]
    public void Catalog_HasNoDuplicateIds()
    {
        var ids = ShortcutCatalog.All.Select(d => d.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Catalog_EveryDefaultGestureParses()
    {
        foreach (var definition in ShortcutCatalog.All)
        {
            var gesture = KeyGesture.Parse(definition.DefaultGesture);
            Assert.NotEqual(default, gesture.Key);
        }
    }

    [Fact]
    public void Catalog_HasNoTwoShortcutsSharingTheirDefaultGesture()
    {
        // Los defaults de fábrica no deben chocar entre sí — si algún día se añade uno nuevo con el mismo
        // gesto que otro ya existente, este test lo detecta antes que un usuario en Ajustes › Atajos.
        var duplicates = ShortcutCatalog.All
            .GroupBy(d => KeyGesture.Parse(d.DefaultGesture), new KeyGestureComparer())
            .Where(g => g.Count() > 1)
            .ToList();

        Assert.Empty(duplicates);
    }

    private sealed class KeyGestureComparer : IEqualityComparer<KeyGesture>
    {
        public bool Equals(KeyGesture? x, KeyGesture? y) => x?.Key == y?.Key && x?.KeyModifiers == y?.KeyModifiers;
        public int GetHashCode(KeyGesture obj) => HashCode.Combine(obj.Key, obj.KeyModifiers);
    }
}
