using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Material.Icons;
using Material.Icons.Avalonia;
using enki.Localization;
using enki.Services;

namespace enki.Views.Editor.Markdown;

/// <summary>
/// El botón de copiar de cada bloque de código: uno por bloque visible, en la esquina superior derecha de
/// su recuadro, que lleva al portapapeles todo lo que hay dentro de las <c>```</c> (sin los delimitadores)
/// y confirma la copia cambiando el icono a una marca durante un momento.
///
/// <para>Los botones no van dentro del editor sino en un lienzo hermano suyo (<c>CodeBlockOverlay</c> en
/// EditorView.axaml), superpuesto al mismo hueco. Hay dos razones y las dos son obligatorias. Una: la línea
/// del delimitador se encoge a una banda de pocos píxeles
/// (<see cref="MarkdownStyle.CodeFenceLineHeight"/>), así que un control metido en ella como elemento de
/// texto volvería a estirarla y desharía justo el aire que se le quitó al bloque. Y dos: las capas propias
/// de AvaloniaEdit (<c>TextView.InsertLayer</c>) solo se añaden a <c>VisualChildren</c>, nunca al árbol
/// lógico, y un control con plantilla que no está en el árbol lógico no llega a recibir su
/// <c>ControlTheme</c> ni los estilos de la app — un <see cref="Button"/> ahí dentro no se dibuja en
/// absoluto. Fuera del editor es un botón normal y corriente, con su estilo, su hover y su tooltip.</para>
///
/// <para>La contrapartida de estar fuera es que hay que traer las coordenadas del
/// <see cref="TextView"/> al lienzo (<see cref="Visual.TranslatePoint"/>); son las mismas coordenadas de
/// ventanilla en las que <see cref="MarkdownBackgroundRenderer"/> dibuja el recuadro. El lienzo se deja sin
/// fondo a propósito: sin fondo no entra en el hit-test, así que todo clic que no caiga en un botón
/// atraviesa la capa y llega al editor como siempre (colocar el cursor, seleccionar).</para>
///
/// <para>Solo se dibuja botón cuando el delimitador de apertura del bloque está a la vista: de un bloque
/// largo del que solo se ve el medio no hay esquina superior donde ponerlo.</para>
/// </summary>
internal sealed class CodeBlockCopyOverlay
{
    /// <summary>Lado del área interactiva y separación con el borde del recuadro. El icono sigue siendo
    /// compacto, pero el objetivo de 28px evita tener que acertar exactamente sobre sus trazos de 12px
    /// para que Avalonia detecte <c>:pointerover</c> y el clic.</summary>
    private const double ButtonSize = 28;
    private const double Inset = 4;

    /// <summary>Cuánto se queda el icono de confirmación antes de volver al de copiar.</summary>
    private static readonly TimeSpan FeedbackDuration = TimeSpan.FromSeconds(1.4);

    private readonly Canvas _host;
    private readonly TextView _textView;
    private readonly LivePreviewState _state;
    private readonly List<CopyButton> _buttons = new();
    private readonly DispatcherTimer _feedbackTimer;
    private CopyButton? _feedbackButton;
    private CopyButton? _pointerButton;

    public CodeBlockCopyOverlay(Canvas host, InputElement inputHost, TextView textView, LivePreviewState state)
    {
        _host = host;
        _textView = textView;
        _state = state;

        _feedbackTimer = new DispatcherTimer { Interval = FeedbackDuration };
        _feedbackTimer.Tick += (_, _) => ClearFeedback();

        // LayoutUpdated y no VisualLinesChanged: hace falta que la posición del TextView dentro de la
        // ventana esté ya calculada para poder traducir coordenadas, y así se cubren de paso el primer
        // dibujado, los cambios de tamaño y el mostrar/ocultar del panel lateral. Reposicionar sin cambios
        // no invalida nada (Avalonia ignora asignar el mismo valor), así que un LayoutUpdated en reposo no
        // realimenta otro.
        textView.LayoutUpdated += (_, _) => Update();
        textView.ScrollOffsetChanged += (_, _) => Update();

        // El Canvas transparente deja pasar correctamente los clics destinados al texto, pero según la
        // plataforma/renderer también puede dejar fuera del hit-test a sus hijos superpuestos. Escuchar
        // en el Grid común resuelve ambos casos: el evento llega aquí tanto si Avalonia encontró el botón
        // como si encontró el editor que hay debajo. Solo se marca como atendido dentro del rectángulo
        // real de uno de los botones; el resto del editor conserva selección, caret y arrastre normales.
        inputHost.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        inputHost.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
        inputHost.PointerExited += (_, _) => SetPointerButton(null);
    }

    /// <summary>Recoloca los botones sobre los bloques que hay ahora mismo en pantalla. Recorre solo las
    /// líneas visibles (mismo coste que el bucle del fondo) y reutiliza los botones ya creados.</summary>
    private void Update()
    {
        var used = 0;
        var width = _textView.Bounds.Width;

        if (_textView.Document is not null && _textView.VisualLinesValid && width > 0)
        {
            var x = width + MarkdownStyle.CodeBlockHorizontalPadding - Inset - ButtonSize;
            var inBlock = false;

            foreach (var visualLine in _textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                var fence = _state.GetFence(lineNumber);
                if (!fence.IsCode)
                {
                    inBlock = false;
                    continue;
                }

                if (inBlock) continue;
                inBlock = true;

                // Solo la apertura del bloque lleva botón, y solo si de verdad empieza aquí: la primera
                // línea de código visible de un bloque a medio desplazar no es su esquina superior.
                if (!fence.IsDelimiter || _state.GetFence(lineNumber - 1).IsCode) continue;

                // Fuera si la esquina del bloque no cabe entera en la ventanilla: el lienzo llega hasta
                // los bordes de la tarjeta, así que un botón colocado en una Y de fuera del texto se
                // quedaría flotando sobre el relleno del editor en vez de recortarse con el texto.
                var y = visualLine.VisualTop - _textView.VerticalOffset + Inset;
                if (y < 0 || y + ButtonSize > _textView.Bounds.Height) continue;
                if (_textView.TranslatePoint(new Point(x, y), _host) is not { } position) continue;

                var button = Rent(used++);
                Bind(button, lineNumber);
                Canvas.SetLeft(button, position.X);
                Canvas.SetTop(button, position.Y);
                button.IsVisible = true;
            }
        }

        for (var i = used; i < _buttons.Count; i++) _buttons[i].IsVisible = false;
        if (_pointerButton is not null && !_pointerButton.IsVisible) SetPointerButton(null);
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e) =>
        SetPointerButton(ButtonAt(e.GetPosition(_host)));

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(_host).Properties.IsLeftButtonPressed) return;
        var button = ButtonAt(e.GetPosition(_host));
        if (button is null) return;

        // Se ejecuta desde el host en fase túnel, sin depender de que el Canvas transparente haya sido
        // el destino del hit-test. Evita también que el mismo gesto coloque el caret bajo el botón.
        e.Handled = true;
        _ = CopyAsync(button);
    }

    private CopyButton? ButtonAt(Point point)
    {
        foreach (var button in _buttons)
        {
            if (!button.IsVisible) continue;
            var left = Canvas.GetLeft(button);
            var top = Canvas.GetTop(button);
            if (double.IsNaN(left) || double.IsNaN(top)) continue;
            if (new Rect(left, top, ButtonSize, ButtonSize).Contains(point)) return button;
        }

        return null;
    }

    private void SetPointerButton(CopyButton? button)
    {
        if (ReferenceEquals(button, _pointerButton)) return;
        _pointerButton?.Classes.Set("proxy-over", false);
        _pointerButton = button;
        _pointerButton?.Classes.Set("proxy-over", true);
    }

    private CopyButton Rent(int index)
    {
        if (index < _buttons.Count) return _buttons[index];

        var button = new CopyButton();
        button.Click += (_, _) => _ = CopyAsync(button);
        _buttons.Add(button);
        _host.Children.Add(button);
        return button;
    }

    /// <summary>Apunta el botón al bloque que empieza en <paramref name="startLine"/>. Si venía enseñando
    /// la confirmación de otro bloque (el botón se reutiliza al desplazar), se le quita: la marca dice
    /// "acabas de copiar esto", y esto ya es otra cosa.</summary>
    private void Bind(CopyButton button, int startLine)
    {
        if (button.StartLine != startLine && ReferenceEquals(button, _feedbackButton)) ClearFeedback();
        button.StartLine = startLine;
    }

    private async Task CopyAsync(CopyButton button)
    {
        var clipboard = TopLevel.GetTopLevel(_host)?.Clipboard;
        if (clipboard is null) return;

        try
        {
            await clipboard.SetTextAsync(BlockText(button.StartLine));
        }
        catch (Exception ex)
        {
            LogService.Warning("No se pudo copiar el bloque de código al portapapeles", ex);
            return;
        }

        ShowFeedback(button);
    }

    /// <summary>El contenido del bloque que abre <paramref name="startLine"/>: las líneas que hay hasta el
    /// siguiente delimitador (o hasta el final del documento, si el bloque quedó sin cerrar), tal cual se
    /// escribieron y sin las <c>```</c>.</summary>
    private string BlockText(int startLine)
    {
        var document = _textView.Document;
        if (document is null || startLine < 1) return string.Empty;

        var builder = new StringBuilder();
        for (var lineNumber = startLine + 1; lineNumber <= document.LineCount; lineNumber++)
        {
            var fence = _state.GetFence(lineNumber);
            if (!fence.IsCode || fence.IsDelimiter) break;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(document.GetText(document.GetLineByNumber(lineNumber)));
        }

        return builder.ToString();
    }

    private void ShowFeedback(CopyButton button)
    {
        ClearFeedback();
        _feedbackButton = button;
        button.ShowCopied(true);
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
    }

    private void ClearFeedback()
    {
        _feedbackTimer.Stop();
        _feedbackButton?.ShowCopied(false);
        _feedbackButton = null;
    }

    /// <summary>El botón en sí. No es enfocable: pulsarlo no debe sacar el foco del editor, que es donde
    /// el usuario está escribiendo.</summary>
    private sealed class CopyButton : Button
    {
        private readonly MaterialIcon _icon = new()
        {
            Kind = MaterialIconKind.ContentCopy,
            Width = 12,
            Height = 12
        };

        public CopyButton()
        {
            Classes.Add("code-copy-btn");
            Content = _icon;
            Width = ButtonSize;
            Height = ButtonSize;
            MinWidth = ButtonSize;
            MinHeight = ButtonSize;
            Focusable = false;
            ShowCopied(false);
        }

        /// <summary>Línea del <c>```</c> que abre el bloque al que este botón está apuntando ahora mismo.</summary>
        public int StartLine { get; set; }

        public void ShowCopied(bool copied)
        {
            _icon.Kind = copied ? MaterialIconKind.Check : MaterialIconKind.ContentCopy;
            ToolTip.SetTip(this, LocalizationService.Instance[
                copied ? "Editor_CodeBlock_Copied_Tooltip" : "Editor_CodeBlock_Copy_Tooltip"]);
        }
    }
}
