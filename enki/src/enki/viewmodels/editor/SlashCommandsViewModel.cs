using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class SlashCommandsViewModel : ObservableObject
{
    /// <summary>Keys stay fixed Spanish identifiers on purpose (docs/agent-docs/internacionalizacion-i18n.md
    /// §5.5) — they're what the user types after "/", never shown on screen, and keeping them stable
    /// across a language change preserves muscle memory. Labels are resolved once here (Fase 2: no
    /// live-refresh-on-language-change wiring yet, that's Fase 4); InsertText for entries with real
    /// language content (links, table headers) is resolved lazily per-insertion via InsertTextKey instead.</summary>
    private static readonly IReadOnlyList<SlashCommand> AllCommands =
    [
        new SlashCommand { Key = "h1", Label = LocalizationService.Instance["Editor_SlashCommand_H1_Label"], Icon = "FormatHeader1", InsertTextLiteral = "# ", CursorOffset = 2 },
        new SlashCommand { Key = "h2", Label = LocalizationService.Instance["Editor_SlashCommand_H2_Label"], Icon = "FormatHeader2", InsertTextLiteral = "## ", CursorOffset = 3 },
        new SlashCommand { Key = "h3", Label = LocalizationService.Instance["Editor_SlashCommand_H3_Label"], Icon = "FormatHeader3", InsertTextLiteral = "### ", CursorOffset = 4 },
        new SlashCommand { Key = "lista", Label = LocalizationService.Instance["Editor_SlashCommand_BulletList_Label"], Icon = "FormatListBulleted", InsertTextLiteral = "- ", CursorOffset = 2 },
        new SlashCommand { Key = "tarea", Label = LocalizationService.Instance["Editor_SlashCommand_TaskList_Label"], Icon = "CheckboxBlankOutline", InsertTextLiteral = "- [ ] ", CursorOffset = 6 },
        new SlashCommand { Key = "cita", Label = LocalizationService.Instance["Editor_SlashCommand_Quote_Label"], Icon = "FormatQuoteClose", InsertTextLiteral = "> ", CursorOffset = 2 },
        new SlashCommand { Key = "enlace", Label = LocalizationService.Instance["Editor_SlashCommand_Link_Label"], Icon = "LinkVariant", InsertTextKey = "Editor_LinkInsert_Placeholder", CursorOffset = 1 },
        new SlashCommand { Key = "codigo", Label = LocalizationService.Instance["Editor_SlashCommand_InlineCode_Label"], Icon = "CodeTags", InsertTextLiteral = "``", CursorOffset = 1 },
        new SlashCommand { Key = "bloque", Label = LocalizationService.Instance["Editor_SlashCommand_CodeBlock_Label"], Icon = "CodeTags", InsertTextLiteral = "```\n\n```", CursorOffset = 4 },
        new SlashCommand { Key = "tabla", Label = LocalizationService.Instance["Editor_SlashCommand_Table_Label"], Icon = "Table", InsertTextKey = "Editor_SlashCommand_Table_InsertText", CursorOffset = 53 },
        new SlashCommand { Key = "fecha", Label = LocalizationService.Instance["Editor_SlashCommand_TodayDate_Label"], Icon = "CalendarToday", InsertTextLiteral = "", CursorOffset = 0 }
    ];

    public ObservableCollection<SlashCommandItemViewModel> FilteredCommands { get; } = [];

    [ObservableProperty]
    private string _filter = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    [ObservableProperty]
    private bool _isOpen;

    public SlashCommandItemViewModel? SelectedCommand => FilteredCommands.Count > 0 && SelectedIndex >= 0 && SelectedIndex < FilteredCommands.Count
        ? FilteredCommands[SelectedIndex]
        : null;

    public event Action<SlashCommand>? CommandSelected;

    partial void OnFilterChanged(string value)
    {
        Refresh();
    }

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            Filter = string.Empty;
            SelectedIndex = 0;
            Refresh();
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (SelectedIndex < 0) SelectedIndex = FilteredCommands.Count - 1;
        if (SelectedIndex >= FilteredCommands.Count) SelectedIndex = 0;
        UpdateSelection();
    }

    [RelayCommand]
    private void Select(SlashCommandItemViewModel item)
    {
        CommandSelected?.Invoke(item.Command);
        IsOpen = false;
    }

    [RelayCommand]
    private void ConfirmSelected()
    {
        if (SelectedCommand is { } cmd) Select(cmd);
    }

    [RelayCommand]
    private void MoveDown()
    {
        if (FilteredCommands.Count == 0) return;
        SelectedIndex = (SelectedIndex + 1) % FilteredCommands.Count;
    }

    [RelayCommand]
    private void MoveUp()
    {
        if (FilteredCommands.Count == 0) return;
        SelectedIndex = (SelectedIndex - 1 + FilteredCommands.Count) % FilteredCommands.Count;
    }

    public void Refresh()
    {
        FilteredCommands.Clear();
        SelectedIndex = 0;

        var trimmed = Filter.Trim().ToLowerInvariant();
        var filtered = string.IsNullOrEmpty(trimmed)
            ? AllCommands
            : AllCommands.Where(c =>
                c.Key.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                c.Label.Contains(trimmed, StringComparison.OrdinalIgnoreCase));

        foreach (var cmd in filtered.Take(10)) FilteredCommands.Add(new SlashCommandItemViewModel(cmd));
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (var i = 0; i < FilteredCommands.Count; i++)
            FilteredCommands[i].IsSelected = i == SelectedIndex;
    }
}
