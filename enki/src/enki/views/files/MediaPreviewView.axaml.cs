using System.ComponentModel;
using Avalonia.Controls;
using enki.ViewModels;
using LibVLCSharp.Avalonia;

namespace enki.Views;

/// <summary>Conecta la superficie nativa únicamente cuando se solicita reproducir.</summary>
public partial class MediaPreviewView : UserControl
{
    private MediaPreviewViewModel? _model;
    private VideoView? _video;

    public MediaPreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        AttachedToVisualTree += (_, _) => Attach();
        DetachedFromVisualTree += (_, _) => Detach();
    }

    private void Attach()
    {
        if (ReferenceEquals(_model, DataContext)) return;
        Detach();
        _model = DataContext as MediaPreviewViewModel;
        if (_model is not null) _model.PropertyChanged += OnChanged;
        UpdateVideo();
    }

    private void Detach()
    {
        if (_video is not null) _video.MediaPlayer = null;
        VideoHost.Content = null;
        _video = null;
        if (_model is null) return;
        _model.PropertyChanged -= OnChanged;
        _model.Close();
        _model = null;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaPreviewViewModel.Player)) UpdateVideo();
    }

    private void UpdateVideo()
    {
        if (_model is { IsVideo: true, Player: { } player })
        {
            _video ??= new VideoView();
            VideoHost.Content = _video;
            _video.MediaPlayer = player;
        }
        else
        {
            if (_video is not null) _video.MediaPlayer = null;
            VideoHost.Content = null;
            _video = null;
        }
    }
}
