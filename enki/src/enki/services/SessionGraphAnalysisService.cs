using System;
using System.Collections.Generic;
using System.Linq;
using enki.Models;

namespace enki.Services;

/// <summary>Deriva conectividad sobre el grafo ya agregado, sin leer archivos ni crear otro almacén.
/// Los grupos son componentes conexas, no categorías semánticas ni sesiones.</summary>
public static class SessionGraphAnalysisService
{
    public static void Analyze(SessionGraph graph)
    {
        var nodes = graph.Nodes.ToDictionary(n => n.GlobalId, StringComparer.OrdinalIgnoreCase);
        var neighbors = nodes.Keys.ToDictionary(id => id,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var node in graph.Nodes)
        {
            node.ConnectionCount = 0;
            node.HasCrossSessionLinks = false;
            node.ClusterId = node.GlobalId;
            node.ClusterSize = 1;
        }

        foreach (var edge in graph.Edges)
        {
            if (!nodes.TryGetValue(edge.FromGlobalId, out var from) ||
                !nodes.TryGetValue(edge.ToGlobalId, out var to) || ReferenceEquals(from, to)) continue;
            neighbors[from.GlobalId].Add(to.GlobalId);
            neighbors[to.GlobalId].Add(from.GlobalId);
            if (!string.Equals(from.SessionId, to.SessionId, StringComparison.OrdinalIgnoreCase))
                from.HasCrossSessionLinks = to.HasCrossSessionLinks = true;
        }

        // Recorrido iterativo O(nodos + enlaces), también para cadenas largas de notas.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        foreach (var node in graph.Nodes)
        {
            node.ConnectionCount = neighbors[node.GlobalId].Count;
            if (!visited.Add(node.GlobalId)) continue;
            var component = new List<SessionGraphNode>();
            var clusterId = node.GlobalId;
            pending.Push(node.GlobalId);
            while (pending.TryPop(out var id))
            {
                component.Add(nodes[id]);
                if (StringComparer.OrdinalIgnoreCase.Compare(id, clusterId) < 0) clusterId = id;
                foreach (var neighbor in neighbors[id])
                    if (visited.Add(neighbor)) pending.Push(neighbor);
            }

            foreach (var member in component)
            {
                member.ClusterId = clusterId;
                member.ClusterSize = component.Count;
            }
        }
    }
}
