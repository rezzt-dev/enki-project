using Avalonia.Controls;

namespace enki.Views;

/// <summary>Inspector de la skill seleccionada, compartido por las dos disposiciones de la sección. Sin
/// code-behind propio: todo lo que hace son bindings contra <see cref="ViewModels.SkillsViewModel"/>.</summary>
public partial class SkillDetailView : UserControl
{
    public SkillDetailView() => InitializeComponent();
}
