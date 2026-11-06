using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using enki.Services;
using enki.Views;
using Xunit;

namespace enki.UITests;

/// <summary>El subrayado del corrector sobre el editor real de AvaloniaEdit (MED-05): marca en segundo plano,
/// sigue las ediciones sin saltar y se apaga con el ajuste. Usa un diccionario mínimo en una carpeta temporal.</summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class SpellCheckUiTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enki-spell-ui-" + Guid.NewGuid().ToString("N"));

    public SpellCheckUiTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "es_ES.aff"), "SET UTF-8\n");
        File.WriteAllText(Path.Combine(_root, "es_ES.dic"), "4\nel\ngato\nperro\ny\n");
        EditorPreferences.SpellCheckEnabled = true;
    }

    public void Dispose()
    {
        EditorPreferences.SpellCheckEnabled = true;
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Bombea el despachador de la interfaz (los temporizadores y las continuaciones de la revisión
    /// en segundo plano) hasta que se cumple la condición o se agota el tiempo. Síncrono a propósito: el
    /// hilo de interfaz del modo headless es el del test.</summary>
    private static bool Wait(Func<bool> condition, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            // El despachador headless solo atiende los temporizadores vencidos cuando hay trabajo
            // pendiente: un trabajo vacío en cada vuelta basta para que los dispare.
            Dispatcher.UIThread.Post(() => { });
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            Thread.Sleep(30);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private (Window Window, TextEditor Editor, SpellCheckController Controller, SpellCheckService Service) Create(string text)
    {
        HeadlessApp.EnsureInitialized();
        var service = new SpellCheckService([_root]);
        Assert.True(service.TrySelectLanguage(CultureInfo.GetCultureInfo("es-ES")));
        var editor = new TextEditor { Document = new TextDocument(text) };
        var window = new Window { Width = 600, Height = 300, Content = editor };
        window.Show();
        var controller = new SpellCheckController(editor.TextArea.TextView, service, followAppLanguage: false);
        editor.TextArea.TextView.BackgroundRenderers.Add(controller);
        controller.Attach(editor.Document);
        return (window, editor, controller, service);
    }

    [Fact]
    public void MisspelledWords_AreMarkedInTheBackground_AndCodeIsLeftAlone()
    {
        var (window, _, controller, _) = Create("el gato y el perroz `gatoz` [[perroz]]");
        try
        {
            Assert.True(Wait(() => controller.MarkedCount == 1));
        }
        finally { controller.Dispose(); window.Close(); }
    }

    [Fact]
    public void Typing_MarksTheNewWord_AndDisablingTheSettingClearsEverything()
    {
        var (window, editor, controller, _) = Create("el gato");
        try
        {
            Assert.True(Wait(() => controller.MarkedCount == 0, 1500));
            editor.Document.Insert(editor.Document.TextLength, " zzzq");
            Assert.True(Wait(() => controller.MarkedCount == 1));

            EditorPreferences.SpellCheckEnabled = false;
            Assert.True(Wait(() => controller.MarkedCount == 0));
        }
        finally { controller.Dispose(); window.Close(); }
    }

    [Fact]
    public void EditingBeforeAMarkedWord_KeepsItMarkedAtTheShiftedPosition()
    {
        var (window, editor, controller, _) = Create("el perroz");
        try
        {
            Assert.True(Wait(() => controller.MarkedCount == 1));
            // Antes de que vuelva a revisar, el tramo sigue a la palabra por sí solo.
            editor.Document.Insert(0, "y el gato ");
            Assert.Equal(1, controller.MarkedCount);
            Assert.True(Wait(() => controller.MarkedCount == 1));
        }
        finally { controller.Dispose(); window.Close(); }
    }

    [Fact]
    public void WithoutADictionary_NothingIsMarked()
    {
        HeadlessApp.EnsureInitialized();
        var service = new SpellCheckService([Path.Combine(_root, "vacia")]);
        var editor = new TextEditor { Document = new TextDocument("palabrotas inventadas") };
        var window = new Window { Width = 400, Height = 200, Content = editor };
        window.Show();
        var controller = new SpellCheckController(editor.TextArea.TextView, service, followAppLanguage: false);
        try
        {
            controller.Attach(editor.Document);
            Wait(() => false, 500);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, controller.MarkedCount);
        }
        finally { controller.Dispose(); window.Close(); }
    }
}
