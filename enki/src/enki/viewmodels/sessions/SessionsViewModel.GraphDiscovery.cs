using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

public sealed partial class SessionsViewModel
{
    /// <summary>Sin etiquetas seleccionadas se muestran todas; varias selecciones se combinan con O.</summary>
    public ObservableCollection<GraphGroupFilterItemViewModel> GraphTagFilters { get; } = [];
    public ObservableCollection<GraphClusterFilterItemViewModel> GraphClusters { get; } = [];
    public bool HasGraphTags => GraphTagFilters.Count > 0;
    public bool HasGraphClusters => GraphClusters.Count > 1;
    public bool IsGraphEmpty => LastGraph is not null && NodeCount == 0;
    public string GraphEmptyMessage => LocalizationService.Instance[
        IsGraphFiltered ? "Sessions_Graph_NoMatches" : "Sessions_Graph_NoNotes"];

    // Propiedad de instancia para bindings y cambio de idioma en caliente.
#pragma warning disable CA1822
    public string[] GraphExplorationOptions =>
    [
        LocalizationService.Instance["Sessions_Graph_AllNotes"],
        LocalizationService.Instance["Sessions_Graph_Orphans"],
        LocalizationService.Instance["Sessions_Graph_CrossSession"]
    ];
#pragma warning restore CA1822

    public string GraphDiscoverySummary => LocalizedTextFormatter.Resolve(LocalizedTextFormatter.Format("Sessions_Graph_DiscoverySummary",
        LastGraph?.Nodes.Count(n => n.ConnectionCount == 0) ?? 0,
        LastGraph?.Edges.Count(e => e.FromSessionId != e.ToSessionId) ?? 0,
        Math.Max(0, GraphClusters.Count - 1)));

    [ObservableProperty]
    private int _graphExplorationIndex;

    [ObservableProperty]
    private GraphClusterFilterItemViewModel? _selectedGraphCluster;

    [ObservableProperty]
    private string _graphSearchText = string.Empty;

    [ObservableProperty]
    private bool _showGraphMesh = true;

    [ObservableProperty]
    private bool _showGraphLabels = true;

    [ObservableProperty]
    private double _graphNodeSize = 1;

    [ObservableProperty]
    private double _graphLinkThickness = 1;

    public event Action? GraphDisplayChanged;
    public event Action<string>? GraphCameraRequested;

    partial void OnGraphSearchTextChanged(string value) => OnGraphFilterToggled();
    partial void OnShowGraphMeshChanged(bool value) => GraphDisplayChanged?.Invoke();
    partial void OnShowGraphLabelsChanged(bool value) => GraphDisplayChanged?.Invoke();
    partial void OnGraphNodeSizeChanged(double value) => GraphDisplayChanged?.Invoke();
    partial void OnGraphLinkThicknessChanged(double value) => GraphDisplayChanged?.Invoke();

    [RelayCommand]
    private void FitGraph() => GraphCameraRequested?.Invoke("fit");

    [RelayCommand]
    private void ZoomInGraph() => GraphCameraRequested?.Invoke("in");

    [RelayCommand]
    private void ZoomOutGraph() => GraphCameraRequested?.Invoke("out");

    [RelayCommand]
    private void IsolateGraphSession(GraphGroupFilterItemViewModel item)
    {
        if (!GraphGroupFilters.Contains(item)) return;
        _updatingGraphFilters = true;
        try
        {
            foreach (var group in GraphGroupFilters) group.IsActive = group == item;
        }
        finally { _updatingGraphFilters = false; }
        OnGraphFilterToggled();
        FitGraph();
    }

    private bool _updatingGraphFilters;

    partial void OnGraphExplorationIndexChanged(int value) => OnGraphFilterToggled();
    partial void OnSelectedGraphClusterChanged(GraphClusterFilterItemViewModel? value) => OnGraphFilterToggled();

    /// <summary>Conserva filtros que siguen existiendo y elimina selecciones obsoletas tras editar notas.
    /// Las opciones siempre proceden del vault completo, nunca del subconjunto visible.</summary>
    private void RebuildGraphDiscoveryFilters(SessionGraph graph)
    {
        _updatingGraphFilters = true;
        try
        {
            var selectedTags = GraphTagFilters.Where(f => f.IsActive).Select(f => f.GroupId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var selectedClusterId = SelectedGraphCluster?.Id;
            GraphTagFilters.Clear();
            foreach (var tag in graph.Nodes.SelectMany(n => n.Tags).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(t => t, StringComparer.CurrentCultureIgnoreCase))
                GraphTagFilters.Add(new GraphGroupFilterItemViewModel(tag, tag,
                    selectedTags.Contains(tag), OnGraphFilterToggled));

            GraphClusters.Clear();
            GraphClusters.Add(new GraphClusterFilterItemViewModel(string.Empty,
                LocalizationService.Instance["Sessions_Graph_AllClusters"]));
            foreach (var group in graph.Nodes.Where(n => n.ClusterSize > 1).GroupBy(n => n.ClusterId)
                         .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var representative = group.OrderByDescending(n => n.ConnectionCount)
                    .ThenBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase).First();
                var label = representative.Title + " · " + representative.SessionName + " · " +
                    Plural.Get("Sessions_Graph_Summary_Notes", group.Count(), group.Count());
                GraphClusters.Add(new GraphClusterFilterItemViewModel(group.Key, label));
            }
            SelectedGraphCluster = GraphClusters.FirstOrDefault(c => c.Id == selectedClusterId) ?? GraphClusters[0];
        }
        finally
        {
            _updatingGraphFilters = false;
        }
        OnPropertyChanged(nameof(HasGraphTags));
        OnPropertyChanged(nameof(HasGraphClusters));
        OnPropertyChanged(nameof(GraphDiscoverySummary));
    }

    private SessionGraph FilterGraph(SessionGraph graph)
    {
        var groups = GraphGroupFilters.Where(f => f.IsActive).Select(f => f.GroupId).ToHashSet();
        var tags = GraphTagFilters.Where(f => f.IsActive).Select(f => f.GroupId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clusterId = SelectedGraphCluster?.Id;
        var terms = GraphSearchText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var nodes = graph.Nodes.Where(n => groups.Contains(n.SessionId)
            && terms.All(term => n.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
                || n.RelativePath.Contains(term, StringComparison.OrdinalIgnoreCase)
                || n.SessionName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || n.Tags.Any(tag => tag.Contains(term.TrimStart('#'), StringComparison.OrdinalIgnoreCase)))
            && (tags.Count == 0 || n.Tags.Any(tags.Contains))
            && (string.IsNullOrEmpty(clusterId) || n.ClusterId == clusterId)
            && (GraphExplorationIndex != 1 || n.ConnectionCount == 0)
            && (GraphExplorationIndex != 2 || n.HasCrossSessionLinks)).ToList();
        var ids = nodes.Select(n => n.GlobalId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var edges = graph.Edges.Where(e => ids.Contains(e.FromGlobalId) && ids.Contains(e.ToGlobalId)
            && (GraphExplorationIndex != 2 || e.FromSessionId != e.ToSessionId)).ToList();
        if (GraphExplorationIndex == 2)
        {
            var endpoints = edges.SelectMany(e => new[] { e.FromGlobalId, e.ToGlobalId })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            nodes.RemoveAll(n => !endpoints.Contains(n.GlobalId));
        }
        return new SessionGraph { Nodes = nodes, Edges = edges };
    }

    [RelayCommand]
    private void ResetGraphFilters()
    {
        _updatingGraphFilters = true;
        try
        {
            foreach (var group in GraphGroupFilters) group.IsActive = true;
            foreach (var tag in GraphTagFilters) tag.IsActive = false;
            GraphSearchText = string.Empty;
            GraphExplorationIndex = 0;
            SelectedGraphCluster = GraphClusters.FirstOrDefault();
        }
        finally
        {
            _updatingGraphFilters = false;
        }
        OnGraphFilterToggled();
    }
}

/// <summary>Opción de navegación derivada de una componente conexa; no almacena ni duplica notas.</summary>
public sealed record GraphClusterFilterItemViewModel(string Id, string Label);
