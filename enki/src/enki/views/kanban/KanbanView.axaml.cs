using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using enki.ViewModels;

namespace enki.Views;

public partial class KanbanView : UserControl
{
    private readonly KanbanCardDragController _cardDrag;

    private KanbanViewModel? _subscribedVm;

    public KanbanView()
    {
        InitializeComponent();
        _cardDrag = new KanbanCardDragController(this, CardDragLayer, CardDragPreview, CardDragImage, CardDropIndicator);
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private KanbanViewModel? Vm => DataContext as KanbanViewModel;

    /// <summary>Grabs initial keyboard focus so the shortcuts below work the moment the page opens,
    /// without requiring the user to click something on it first — same pattern as CalendarView_Loaded.</summary>
    private void KanbanView_Loaded(object? sender, RoutedEventArgs e) => Focus();

    /// <summary>Atajos de teclado del tablero:
    /// N nueva tarjeta, C nueva columna (Fase 7 de docs/agent-docs/analisis-proyecto/roadmap.md), Delete
    /// borrar la tarjeta enfocada, Esc cerrar el diálogo abierto (o, si no hay ninguno, limpiar el
    /// buscador), Ctrl+F saltar al buscador. Enter ya abre el detalle sin código adicional: cada tarjeta
    /// es un Button enfocable, así que Avalonia dispara su Command con Enter/Espacio de forma nativa.
    /// Se anuncian en la interfaz desde el aviso de tablero vacío y desde los tooltips de "nueva
    /// columna" y "añadir tarjeta", en vez de existir solo en este comentario.</summary>
    private void KanbanView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control)
        {
            // Con la ventana de archivadas abierta, el buscador que el usuario tiene delante es el suyo,
            // no el del tablero que hay detrás.
            var searchBox = vm.IsArchiveOpen ? ArchiveSearchBox : KanbanSearchBox;
            searchBox.Focus();
            searchBox.SelectAll();
            e.Handled = true;
            return;
        }

        // No secuestrar los campos de texto (buscador, título de tarjeta/columna, etiquetas) — Escape
        // sigue cerrando el diálogo abierto, pero el resto de atajos de una sola tecla solo aplican
        // cuando el usuario no está escribiendo.
        if (e.Source is TextBox) { if (e.Key != Key.Escape) return; }

        switch (e.Key)
        {
            case Key.N when vm.DisplayMode == KanbanDisplayMode.Board && vm.CardEditor is null && vm.CardDetail is null && !vm.IsArchiveOpen:
                if (TargetColumnForNewCard(vm, e.Source as Visual) is { } column) vm.OpenCreateCardCommand.Execute(column);
                e.Handled = true;
                break;

            case Key.C when vm.DisplayMode == KanbanDisplayMode.Board && vm.CardEditor is null && vm.CardDetail is null && !vm.IsArchiveOpen:
                vm.AddColumnCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Delete:
                if (e.Source is Control { DataContext: KanbanCardViewModel card })
                {
                    // Desde la ventana de archivadas se borra por el mismo camino, pero pasando por el
                    // comando que sabe cerrarla y volver a abrirla alrededor de la confirmación.
                    if (vm.IsArchiveOpen) vm.RequestDeleteArchivedCardCommand.Execute(card);
                    else vm.RequestDeleteCardCommand.Execute(card);
                    e.Handled = true;
                }
                break;

            // Orden de cierre = orden de apilado de los diálogos: el más "encima" primero. El de borrado
            // de columna faltaba en esta lista y era el único modal del tablero del que Escape no sacaba.
            // Sin ningún diálogo abierto, Escape limpia el buscador, que es el gesto que espera quien
            // acaba de filtrar y quiere el tablero entero de vuelta.
            case Key.Escape:
                if (vm.CardPendingDelete is not null) { vm.CancelDeleteCardCommand.Execute(null); e.Handled = true; }
                else if (vm.ColumnPendingDelete is not null) { vm.CancelDeleteColumnCommand.Execute(null); e.Handled = true; }
                else if (vm.IsArchiveAllPending) { vm.CancelArchiveAllDoneCommand.Execute(null); e.Handled = true; }
                else if (vm.CardEditor is not null) { vm.CancelCardEditorCommand.Execute(null); e.Handled = true; }
                else if (vm.CardDetail is not null) { vm.CloseCardDetailCommand.Execute(null); e.Handled = true; }
                // La ventana de archivadas va después del detalle y del editor porque los tres pueden
                // encadenarse: se abren desde ella y la dejan esperando debajo.
                else if (vm.IsArchiveOpen) { vm.CloseArchiveCommand.Execute(null); e.Handled = true; }
                else if (vm.HasActiveFilters) { vm.ClearFiltersCommand.Execute(null); e.Handled = true; }
                break;
        }
    }

    /// <summary>Columna en la que aterriza la tarjeta que crea el atajo "N": la del elemento enfocado si
    /// lo hay (una tarjeta, o cualquier control dentro de una columna), y la primera del tablero como
    /// último recurso. Antes iba siempre a la primera columna, que con el foco puesto en "En progreso"
    /// es justo donde el usuario no la quería. El origen es <c>e.Source</c> del propio KeyDown, que en
    /// una pulsación de teclado es ya el elemento enfocado — no hace falta consultar el FocusManager.</summary>
    private static KanbanColumnViewModel? TargetColumnForNewCard(KanbanViewModel vm, Visual? focused)
    {
        for (; focused is not null; focused = focused.GetVisualParent())
        {
            switch ((focused as Control)?.DataContext)
            {
                case KanbanColumnViewModel column: return column;
                case KanbanCardViewModel card: return FindColumn(vm, card);
            }
        }

        return vm.Columns.Count > 0 ? vm.Columns[0] : null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        _cardDrag.Cancel();
        Unsubscribe();
        _subscribedVm = Vm;
        if (_subscribedVm is null) return;
        _subscribedVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = null;
    }

    // Focuses the title field the moment the create/edit popup opens, same "ready to type
    // immediately" courtesy as every other inline editor in this view.
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(KanbanViewModel.DisplayMode) or nameof(KanbanViewModel.BoardOrientation)
            or nameof(KanbanViewModel.CardEditor) or nameof(KanbanViewModel.CardDetail)
            or nameof(KanbanViewModel.IsArchiveOpen))
            _cardDrag.Cancel();

        if (e.PropertyName != nameof(KanbanViewModel.CardEditor)) return;
        if (Vm is not { CardEditor: not null }) return;

        Dispatcher.UIThread.Post(() =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        });
    }

    private static KanbanColumnViewModel? FindColumn(KanbanViewModel vm, KanbanCardViewModel card)
    {
        foreach (var column in vm.Columns)
            if (column.Cards.Contains(card)) return column;
        return null;
    }

    // ===================== Inline editing: column title =====================

    private void ColumnTitleEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KanbanColumnViewModel column }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameColumnCommand.Execute(column);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            column.CancelRenameCommand.Execute(null);
        }
    }

    private void ColumnTitleEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: KanbanColumnViewModel { IsRenaming: true } column })
            Vm?.CommitRenameColumnCommand.Execute(column);
    }

    private void ColumnTitleEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusAndSelect(textBox);
    }

    private void ColumnTitleEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusAndSelect(textBox);
    }

    // ===================== Card editor popup: new subtask field =====================

    private void NewSubtaskBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KanbanCardEditorViewModel editor }) return;
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (editor.AddSubtaskCommand.CanExecute(null)) editor.AddSubtaskCommand.Execute(null);
    }

    // ===================== "Gestión de tareas" task editor popup: new subtask field =====================

    private void PolarNewSubtaskBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PolarTaskEditorViewModel editor }) return;
        if (e.Key != Key.Enter) return;

        e.Handled = true;
        if (editor.AddSubtaskCommand.CanExecute(null)) editor.AddSubtaskCommand.Execute(null);
    }

    private static void FocusAndSelect(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }
}
