using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;

namespace enki.Views;

/// <summary>Construye cada sección al abrirla por primera vez —o al precargarla al arrancar— y conserva su
/// árbol visual durante la vida del vault. Las secciones ocultas no participan en medida, dibujo ni entrada;
/// mantenerlas ancladas evita destruir las cachés nativas del editor al navegar.</summary>
public sealed class SectionHost : Panel
{
    public static readonly StyledProperty<object?> PageProperty =
        AvaloniaProperty.Register<SectionHost, object?>(nameof(Page));
    public static readonly StyledProperty<object?> ScopeProperty =
        AvaloniaProperty.Register<SectionHost, object?>(nameof(Scope));

    private readonly Dictionary<Type, Control> _views = [];
    private Control? _activeView;

    public object? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }
    public object? Scope { get => GetValue(ScopeProperty); set => SetValue(ScopeProperty, value); }

    /// <summary>Construye la vista de <paramref name="page"/> sin mostrarla, para que al navegar hacia ella
    /// solo haya que hacerla visible. Si la sección ya tiene vista, no hace nada.</summary>
    public void Preload(object page) => GetOrBuildView(page);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScopeProperty)
        {
            ClearViews();
            // El binding de Page puede cambiar en el mismo ciclo al abrir otro vault.
            Dispatcher.UIThread.Post(ShowPage, DispatcherPriority.Loaded);
        }
        else if (change.Property == PageProperty) ShowPage();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ShowPage();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ClearViews();
        base.OnDetachedFromVisualTree(e);
    }

    private void ClearViews()
    {
        Children.Clear();
        _views.Clear();
        _activeView = null;
    }

    private void ShowPage()
    {
        if (Page is not { } page)
        {
            if (_activeView is not null) _activeView.IsVisible = false;
            _activeView = null;
            return;
        }

        if (GetOrBuildView(page) is not { } view) return;
        if (ReferenceEquals(_activeView, view)) return;
        if (_activeView is not null) _activeView.IsVisible = false;
        _activeView = view;
        view.IsVisible = true;
    }

    /// <summary>Devuelve la vista ya construida para <paramref name="page"/> o la crea oculta y la ancla.
    /// Reconstruye la vista si el DataContext guardado apunta a otra instancia del ViewModel.</summary>
    private Control? GetOrBuildView(object page)
    {
        var type = page.GetType();
        if (_views.TryGetValue(type, out var view))
        {
            if (ReferenceEquals(view.DataContext, page)) return view;
            Children.Remove(view);
            _views.Remove(type);
            if (ReferenceEquals(_activeView, view)) _activeView = null;
        }

        var template = this.FindDataTemplate(page);
        if (template?.Build(page) is not { } built) return null;
        built.DataContext = page;
        built.IsVisible = false;
        _views.Add(type, built);
        Children.Add(built);
        return built;
    }
}
