using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace enki.Views;

/// <summary>Pinta las líneas de un diff (<see cref="Services.DiffLine"/>). Sin lógica propia: la lista llega por
/// <see cref="Lines"/>.</summary>
public partial class AssistantDiffView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> LinesProperty =
        AvaloniaProperty.Register<AssistantDiffView, IEnumerable?>(nameof(Lines));

    public AssistantDiffView() => InitializeComponent();

    public IEnumerable? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }
}
