using Avalonia.Controls;

namespace enki.Views;

/// <summary>Contenido del calendario de constancia de la nota diaria. Sin lógica propia: es una vista
/// declarativa sobre <see cref="ViewModels.NotesViewModel"/>, compartida por las dos disposiciones de
/// Documentos (ver DailyConsistencyView.axaml).</summary>
public partial class DailyConsistencyView : UserControl
{
    public DailyConsistencyView() => InitializeComponent();
}
