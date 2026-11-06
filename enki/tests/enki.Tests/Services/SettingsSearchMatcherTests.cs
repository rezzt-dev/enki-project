using enki.Services;
using Xunit;

namespace enki.Tests.Services;

public class SettingsSearchMatcherTests
{
    [Theory]
    [InlineData("NOTIFICACION", "Notificación", true)]
    [InlineData("notificacio\u0301n", "NOTIFICACIÓN", true)]
    [InlineData("tamano fuente", "Tamaño de la fuente", true)]
    [InlineData("  fuente   TAMANO  ", "Tamaño de la fuente", true)]
    [InlineData("étiquettes", "ÉTIQUETTES des cartes", true)]
    [InlineData("ubersicht", "Übersicht", true)]
    [InlineData("font size", "Tamaño de la fuente", false)]
    [InlineData("fuente volumen", "Tamaño de la fuente", false)]
    [InlineData("", "Cualquier ajuste", true)]
    [InlineData("  ", "Cualquier ajuste", true)]
    public void Matches_NormalizesAllWords_WithoutTranslatingTheQuery(string query, string text, bool expected)
    {
        Assert.Equal(expected, SettingsSearchMatcher.Matches(query, [text]));
    }

    [Fact]
    public void Matches_CanCombineSectionAndOptionText()
    {
        Assert.True(SettingsSearchMatcher.Matches("kanban etiquetas", ["Kanban", "Mostrar etiquetas"]));
    }
}
