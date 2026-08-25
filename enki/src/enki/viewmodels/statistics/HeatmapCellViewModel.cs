using System;

namespace enki.ViewModels;

/// <summary>One cell of the 12-week × 7-day habit consistency heatmap — drives background colour and a
/// tooltip that shows the date plus whether habits were completed that day.</summary>
public sealed record HeatmapCellViewModel(DateOnly Date, int State, string Label);