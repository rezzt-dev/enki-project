using System;
using System.IO;
using enki.Localization;
using enki.Services;
using enki.ViewModels;
using Xunit;

namespace enki.Tests.ViewModels;

/// <summary>Covers <see cref="ThemeEditorViewModel.HasLowContrastWarning"/> (análisis alfa-3 §4.5, Alta —
/// el editor de temas personalizados no ofrecía ninguna advertencia si el usuario dejaba el texto casi
/// ilegible sobre su propio fondo). Same throwaway-directory pattern as <c>CustomThemeServiceTests</c>.</summary>
public class ThemeEditorViewModelTests : IDisposable
{
    private readonly string _storageDirectory =
        Path.Combine(Path.GetTempPath(), "enki-tests-theme-editor-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_storageDirectory)) Directory.Delete(_storageDirectory, recursive: true);
    }

    private ThemeEditorViewModel CreateEditor()
    {
        var settingsService = new SettingsService(_storageDirectory);
        var customThemeService = new CustomThemeService(settingsService);
        var notificationService = new NotificationService(settingsService, _storageDirectory);
        var theme = customThemeService.Create("Test theme");

        return new ThemeEditorViewModel(
            theme,
            customThemeService,
            notificationService,
            LocalizationService.Instance,
            isActive: false,
            onSaved: () => { },
            onCancelled: () => { },
            onDuplicateOpened: _ => { });
    }

    private static ThemeColorSlotViewModel FindSlot(ThemeEditorViewModel editor, string resourceKey)
    {
        foreach (var group in editor.Groups)
        foreach (var slot in group.Slots)
        {
            if (slot.ResourceKey == resourceKey) return slot;
        }
        throw new InvalidOperationException($"Slot '{resourceKey}' not found.");
    }

    [Fact]
    public void HasLowContrastWarning_DefaultTheme_IsFalse()
    {
        var editor = CreateEditor();

        Assert.False(editor.HasLowContrastWarning);
    }

    [Fact]
    public void HasLowContrastWarning_TextColorMatchesBackground_BecomesTrue()
    {
        var editor = CreateEditor();

        FindSlot(editor, "TextPrimaryColor").SetHex("#121212");
        FindSlot(editor, "AppBackgroundColor").SetHex("#121212");

        Assert.True(editor.HasLowContrastWarning);
    }

    [Fact]
    public void HasLowContrastWarning_ResetToDefaults_ClearsWarning()
    {
        var editor = CreateEditor();
        FindSlot(editor, "TextPrimaryColor").SetHex("#121212");
        FindSlot(editor, "AppBackgroundColor").SetHex("#121212");
        Assert.True(editor.HasLowContrastWarning);

        editor.ResetToDefaultsCommand.Execute(null);

        Assert.False(editor.HasLowContrastWarning);
    }
}
