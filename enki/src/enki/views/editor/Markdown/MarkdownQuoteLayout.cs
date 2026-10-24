using System;

namespace enki.Views.Editor.Markdown;

/// <summary>Geometría horizontal compartida por el formateo del texto y el dibujo de las barras de cita.
/// Las medidas son unidades lógicas de Avalonia, por lo que escalan con el DPI sin depender del ancho de
/// la ventana.</summary>
internal static class MarkdownQuoteLayout
{
    public const double RailThickness = 2;
    public const double GapToText = 12;
    public const double LevelWidth = RailThickness + GapToText;

    public static double ContentIndent(int depth) => Math.Max(0, depth) * LevelWidth;

    public static double RailX(int level) => Math.Max(0, level) * LevelWidth;
}
