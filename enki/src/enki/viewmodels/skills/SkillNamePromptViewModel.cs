using CommunityToolkit.Mvvm.ComponentModel;

namespace enki.ViewModels;

/// <summary>Estado del pequeño diálogo modal para pedir un nombre (duplicar skill / crear copia editable
/// de una default) — mismo patrón "propiedad anulable = modal abierto" que
/// <c>KanbanViewModel.CardPendingDelete</c>, con un campo de texto en vez de una simple confirmación.</summary>
public sealed partial class SkillNamePromptViewModel : ObservableObject
{
    public string Title { get; }
    public string ConfirmLabel { get; }

    [ObservableProperty]
    private string _inputText;

    [ObservableProperty]
    private string? _errorMessage;

    public SkillNamePromptViewModel(string title, string confirmLabel, string initialText)
    {
        Title = title;
        ConfirmLabel = confirmLabel;
        _inputText = initialText;
    }
}
