using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using enki.Localization;
using enki.Models;
using enki.ViewModels;
using enki.Views.Sessions;

namespace enki.Views;

public partial class SessionsView : UserControl
{
    /// <summary>Safety net for the loading skeleton (see <see cref="OnGraphReady"/>/<see cref="OnGraphWebViewReady"/>):
    /// if the WebView never posts back its "ready" message — a JS error, an engine that silently fails to
    /// navigate — the skeleton would otherwise spin forever instead of just falling back to showing
    /// whatever did (or didn't) render.</summary>
    private static readonly TimeSpan GraphLoadingSafetyTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Shortest time the loading screen stays up once shown — long enough to read as a deliberate
    /// transition rather than a flicker when the graph comes back almost immediately.</summary>
    private static readonly TimeSpan GraphLoadingMinimumVisible = TimeSpan.FromMilliseconds(520);

    /// <summary>Must match the Opacity <c>DoubleTransition</c> on <c>GraphLoadingOverlay</c> in the XAML.</summary>
    private static readonly TimeSpan GraphLoadingFadeDuration = TimeSpan.FromMilliseconds(360);

    private NativeWebView? _graphWebView;
    private SessionsViewModel? _subscribedVm;
    private DispatcherTimer? _graphLoadingSafetyTimer;
    private IDisposable? _graphLoadingHideStep;
    private readonly Stopwatch _graphLoadingVisibleFor = new();

    public SessionsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) =>
        {
            Unsubscribe();
            _graphLoadingSafetyTimer?.Stop();
            _graphLoadingSafetyTimer = null;
            _graphLoadingHideStep?.Dispose();
            _graphLoadingHideStep = null;
            GraphLoadingAnimation.IsActive = false;
            DisposeGraphWebView();
        };
    }

    private SessionsViewModel? Vm => DataContext as SessionsViewModel;

    private void SessionsView_Loaded(object? sender, RoutedEventArgs e) => Focus();

    // Escape closes whichever of the four session dialogs (delete confirmation, unlock/forgot-password,
    // recovery codes, create session) is currently on top — same priority-order pattern as
    // KanbanView_KeyDown, since these dialogs can't stack but a consistent "most specific first" order
    // keeps this handler correct if that ever changes. Unlike Kanban's handler, this one only ever acts on
    // Escape, so it doesn't need to guard against hijacking other keys typed into a focused TextBox.
    private void SessionsView_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key != Key.Escape) return;

        if (vm.SessionPendingDelete is not null)
        {
            vm.CancelDeleteSessionCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.SessionPendingUnlock is not null)
        {
            vm.CancelUnlockCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.IsRecoveryCodesDialogOpen)
        {
            vm.CloseRecoveryCodesDialogCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.IsCreateDialogOpen)
        {
            vm.CancelCreateSessionCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        _subscribedVm = Vm;
        if (_subscribedVm is null) return;

        _subscribedVm.GraphReady += OnGraphReady;
        _subscribedVm.GraphUpdated += OnGraphUpdated;
        _subscribedVm.LinkRejected += OnLinkRejected;
        _subscribedVm.PendingLinkChanged += OnPendingLinkChanged;
        _subscribedVm.PropertyChanged += OnVmPropertyChanged;

        // The VM already computed (and raised) its initial graph before this view existed to hear it —
        // re-issue it now that something is finally subscribed, instead of leaving the panel blank.
        // RepublishGraph reuses that already-computed graph instead of redoing the vault-wide scan.
        _subscribedVm.RepublishGraph();
    }

    private void Unsubscribe()
    {
        if (_subscribedVm is null) return;
        _subscribedVm.GraphReady -= OnGraphReady;
        _subscribedVm.GraphUpdated -= OnGraphUpdated;
        _subscribedVm.LinkRejected -= OnLinkRejected;
        _subscribedVm.PendingLinkChanged -= OnPendingLinkChanged;
        _subscribedVm.PropertyChanged -= OnVmPropertyChanged;
        _subscribedVm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SessionsViewModel.IsCreateDialogOpen)) return;
        if (Vm is not { IsCreateDialogOpen: true }) return;

        Dispatcher.UIThread.Post(() =>
        {
            NewSessionNameBox.Focus();
            NewSessionNameBox.SelectAll();
        });
    }

    private void NewSessionNameBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (vm.ConfirmCreateSessionCommand.CanExecute(null)) vm.ConfirmCreateSessionCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CancelCreateSessionCommand.Execute(null);
        }
    }

    // ===================== Session list =====================

    private void SessionRow_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: SessionListItemViewModel item } && Vm is { } vm)
            vm.SelectSessionCommand.Execute(item);
    }

    private void SessionNameEdit_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionListItemViewModel item }) return;

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Vm?.CommitRenameSessionCommand.Execute(item);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            item.IsRenaming = false;
        }
    }

    private void SessionNameEdit_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SessionListItemViewModel { IsRenaming: true } item })
            Vm?.CommitRenameSessionCommand.Execute(item);
    }

    // Same dual-hook rationale as NotesView's folder rename box: a brand-new row's TextBox is already
    // visible the moment it's realized, so only Initialized sees it; an existing row's TextBox toggles
    // visibility later, so only PropertyChanged sees that one.
    private void SessionNameEdit_Initialized(object? sender, EventArgs e)
    {
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private void SessionNameEdit_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;
        if (sender is TextBox { IsVisible: true } textBox) FocusRenameBox(textBox);
    }

    private static void FocusRenameBox(TextBox textBox)
    {
        Dispatcher.UIThread.Post(() =>
        {
            textBox.Focus();
            textBox.SelectAll();
        });
    }

    // ===================== Graph WebView host =====================

    private NativeWebView? EnsureGraphView()
    {
        if (_graphWebView is not null) return _graphWebView;
        if (!WebViewHost.IsEngineAvailable()) return null;

        var webView = new NativeWebView
        {
            Background = Application.Current?.TryFindResource("AppBackgroundAltBrush", out var bg) == true
                ? bg as IBrush
                : null
        };
        webView.WebMessageReceived += OnWebMessageReceived;
        GraphContentHost.Content = webView;
        _graphWebView = webView;
        return webView;
    }

    /// <summary>Fase 10 (docs/analisis-proyecto/roadmap.md — "evaluar si el WebView del grafo de sesiones
    /// necesita disposición explícita al salir de la pestaña repetidamente"). La respuesta, sin necesitar
    /// perfilar memoria en vivo: sí. <see cref="MainWindow"/> hospeda cada página vía
    /// <c>ContentControl.Content</c> + <c>DataTemplate</c> (MainWindow.axaml), y ese mecanismo reconstruye
    /// la vista desde cero cada vez que se vuelve a la pestaña — no la mantiene viva en segundo plano ni la
    /// reutiliza por identidad del ViewModel. Cada entrada a Sesiones crea, por tanto, una
    /// <see cref="NativeWebView"/> nueva (un motor de navegador nativo real: WebView2/WebKitGTK/WKWebView,
    /// no un control ligero de Avalonia), y <c>Avalonia.Controls.WebView.dll</c> expone un
    /// <c>System.IDisposable.Dispose()</c> real en su implementación (verificado contra el ensamblado del
    /// paquete, versión 12.0.1) para liberar ese recurso nativo — que Avalonia no llama por sí sola al
    /// desconectar el control del árbol visual. Sin esta llamada, cada ciclo de entra/sale a la pestaña
    /// dejaba un motor de navegador nativo completo huérfano, a la espera de que algún finalizador (si lo
    /// hay) lo recogiera en un momento indeterminado — el patrón de fuga clásico de controles nativos
    /// embebidos. <c>as IDisposable</c> en vez de un cast directo: no ata este código a si
    /// <see cref="NativeWebView"/> implementa la interfaz en sí misma o en un tipo base interno del
    /// paquete.</summary>
    private void DisposeGraphWebView()
    {
        if (_graphWebView is not { } webView) return;

        webView.WebMessageReceived -= OnWebMessageReceived;
        (webView as IDisposable)?.Dispose();
        _graphWebView = null;
    }

    private void OnGraphReady(SessionGraph graph)
    {
        if (EnsureGraphView() is not { } webView)
        {
            GraphContentHost.Content = WebViewHost.BuildUnavailablePanel(LocalizationService.Instance["Sessions_Graph_Unavailable"]);
            return;
        }

        ShowGraphLoadingOverlay();
        var html = SessionGraphRenderer.RenderDocument(graph);
        webView.NavigateToString(html, null);
    }

    private void ShowGraphLoadingOverlay()
    {
        _graphLoadingHideStep?.Dispose();
        _graphLoadingHideStep = null;

        if (!GraphLoadingOverlay.IsVisible)
        {
            GraphLoadingOverlay.IsVisible = true;
            GraphLoadingAnimation.IsActive = true;
            _graphLoadingVisibleFor.Restart();
        }
        // Fades in from 0 on a fresh show, or back up if a previous fade-out was still running.
        GraphLoadingOverlay.Opacity = 1;

        _graphLoadingSafetyTimer?.Stop();
        _graphLoadingSafetyTimer = new DispatcherTimer { Interval = GraphLoadingSafetyTimeout };
        _graphLoadingSafetyTimer.Tick += (_, _) => HideGraphLoadingOverlay();
        _graphLoadingSafetyTimer.Start();
    }

    /// <summary>Holds the loading screen for at least <see cref="GraphLoadingMinimumVisible"/> (a graph
    /// that's ready almost instantly would otherwise flash the animation for a single frame), then fades
    /// it out and only stops the animation once it's fully transparent.</summary>
    private void HideGraphLoadingOverlay()
    {
        _graphLoadingSafetyTimer?.Stop();
        _graphLoadingSafetyTimer = null;
        if (!GraphLoadingOverlay.IsVisible || _graphLoadingHideStep is not null) return;

        var remaining = GraphLoadingMinimumVisible - _graphLoadingVisibleFor.Elapsed;
        _graphLoadingHideStep = DispatcherTimer.RunOnce(BeginGraphLoadingFadeOut,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1));
    }

    private void BeginGraphLoadingFadeOut()
    {
        GraphLoadingOverlay.Opacity = 0;
        _graphLoadingHideStep = DispatcherTimer.RunOnce(() =>
        {
            _graphLoadingHideStep = null;
            GraphLoadingOverlay.IsVisible = false;
            GraphLoadingAnimation.IsActive = false;
        }, GraphLoadingFadeDuration);
    }

    /// <summary>The WebView's own document reported it finished reading its initial nodes/edges into sigma
    /// (the <c>{ type: 'ready' }</c> message posted right after <c>s.graph.read</c> in
    /// SessionGraphRenderer) — dismisses the loading skeleton a beat earlier than waiting for the
    /// never-ending animation loop to draw its first frame would.</summary>
    private void OnGraphWebViewReady() => HideGraphLoadingOverlay();

    private void OnGraphUpdated(SessionGraph graph)
    {
        if (_graphWebView is null) return;
        _ = _graphWebView.InvokeScript(SessionGraphRenderer.BuildSetGraphDataScript(graph));
    }

    // Link mode itself no longer needs to reach the WebView at all — SessionsViewModel.HandleNodeClicked
    // judges every click against IsLinkMode host-side. Turning link mode off still clears the pending-pick
    // pulse (see SessionsViewModel.ToggleLinkMode), which arrives here through PendingLinkChanged below like
    // any other pending-pick change.
    private void OnPendingLinkChanged(string? globalId)
    {
        if (_graphWebView is null) return;
        _ = _graphWebView.InvokeScript(SessionGraphRenderer.BuildSetPendingLinkScript(globalId));
    }

    private void OnLinkRejected(string fromGlobalId, string toGlobalId)
    {
        if (_graphWebView is null) return;
        _ = _graphWebView.InvokeScript(SessionGraphRenderer.BuildLinkRejectedScript(fromGlobalId, toGlobalId));
    }

    private void OnWebMessageReceived(object? sender, WebMessageReceivedEventArgs e)
    {
        var body = e.Body;
        if (string.IsNullOrWhiteSpace(body)) return;

        // WebMessageReceived isn't guaranteed to land on the UI thread across every native adapter, and
        // the handler below both touches bound VM state and calls back into the WebView.
        Dispatcher.UIThread.Post(() => HandleGraphMessage(body));
    }

    private void HandleGraphMessage(string json)
    {
        if (Vm is not { } vm) return;

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("type", out var typeProp)) return;

            switch (typeProp.GetString())
            {
                case "ready":
                    OnGraphWebViewReady();
                    break;

                // A single message for every click, whatever mode the page thinks it's in — the WebView no
                // longer decides open-vs-link itself (see SessionGraphRenderer's handleNodeClick and the doc
                // comment on SessionsViewModel.HandleNodeClicked for why that decision moved host-side).
                case "nodeClick":
                    if (document.RootElement.TryGetProperty("path", out var pathProp) && pathProp.GetString() is { } path)
                        vm.HandleNodeClicked(path);
                    break;

                // Sent alongside (never instead of) "nodeClick" for the second click of a double click —
                // see handleNodeDoubleClick in SessionGraphRenderer.
                case "nodeDoubleClick":
                    if (document.RootElement.TryGetProperty("path", out var dblPathProp) && dblPathProp.GetString() is { } dblPath)
                        vm.HandleNodeDoubleClicked(dblPath);
                    break;

                case "positions":
                    if (document.RootElement.TryGetProperty("nodes", out var nodesProp) && nodesProp.ValueKind == JsonValueKind.Array)
                    {
                        var positions = new List<(string RelativePath, double X, double Y, double Z)>();
                        foreach (var node in nodesProp.EnumerateArray())
                        {
                            if (node.TryGetProperty("path", out var p) && p.GetString() is { } relativePath
                                && node.TryGetProperty("x", out var x) && node.TryGetProperty("y", out var y) && node.TryGetProperty("z", out var z))
                            {
                                positions.Add((relativePath, x.GetDouble(), y.GetDouble(), z.GetDouble()));
                            }
                        }
                        vm.HandlePositionsReported(positions);
                    }
                    break;
            }
        }
    }
}
