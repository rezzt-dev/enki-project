using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using enki.Localization;
using enki.Models;

namespace enki.ViewModels;

/// <summary>Una burbuja del chat. Envuelve el <see cref="AiChatMessage"/> real que se guarda en el historial
/// — no una copia —, así que lo que se escribe en <see cref="Text"/> durante el streaming es lo mismo que se
/// persiste al terminar el turno.</summary>
public sealed partial class AiChatMessageViewModel : ObservableObject
{
    public AiChatMessageViewModel(AiChatMessage model)
    {
        Model = model;
        _text = model.Text;
        Attachments = model.Attachments.Select(a => new AiAttachmentChipViewModel(a)).ToList();
    }

    public AiChatMessage Model { get; }

    public bool IsUser => Model.Role == AiChatRole.User;
    public bool IsAssistant => Model.Role == AiChatRole.Assistant;

    public IReadOnlyList<AiAttachmentChipViewModel> Attachments { get; }
    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText), nameof(CanAct))]
    private string _text;

    /// <summary>La respuesta está llegando todavía.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAct), nameof(IsThinking))]
    private bool _isStreaming;

    /// <summary>Hay una respuesta en camino pero aún no ha llegado ni una palabra.</summary>
    public bool IsThinking => IsStreaming && !HasText;

    [ObservableProperty]
    private bool _isInterrupted;

    /// <summary>Confirmación breve tras copiar la respuesta, en el propio botón.</summary>
    [ObservableProperty]
    private bool _isCopied;

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>Las acciones (copiar, guardar como nota) solo tienen sentido sobre una respuesta terminada.</summary>
    public bool CanAct => IsAssistant && !IsStreaming && HasText;

    public string TimeLabel => RelativeTimeFormatter.FormatClockOrShortDate(Model.CreatedUtc);

    partial void OnTextChanged(string value)
    {
        Model.Text = value;
        OnPropertyChanged(nameof(IsThinking));
    }

    partial void OnIsInterruptedChanged(bool value) => Model.IsInterrupted = value;

    public void RefreshLocalizedLabels()
    {
        OnPropertyChanged(nameof(TimeLabel));
        foreach (var attachment in Attachments) attachment.RefreshLocalizedLabels();
    }
}
