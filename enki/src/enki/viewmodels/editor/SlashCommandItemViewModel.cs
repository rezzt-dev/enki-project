using CommunityToolkit.Mvvm.ComponentModel;
using enki.Models;

namespace enki.ViewModels;

public sealed partial class SlashCommandItemViewModel : ObservableObject
{
    public SlashCommand Command { get; }

    [ObservableProperty]
    private bool _isSelected;

    public string Key => Command.Key;
    public string Label => Command.Label;
    public string Icon => Command.Icon;
    public string InsertText => Command.InsertText;
    public int CursorOffset => Command.CursorOffset;

    public SlashCommandItemViewModel(SlashCommand command)
    {
        Command = command;
    }
}
