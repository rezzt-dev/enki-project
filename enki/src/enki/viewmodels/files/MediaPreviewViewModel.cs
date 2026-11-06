using System;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;
using LibVLCSharp.Shared;

namespace enki.ViewModels;

/// <summary>Reproductor local bajo demanda. Conserva la copia descifrada solo mientras el visor está abierto.</summary>
public sealed partial class MediaPreviewViewModel : ObservableObject, IDisposable
{
    private readonly VaultService _vault;
    private readonly string _path;
    private readonly Action<string> _reportError;
    private LibVLC? _engine;
    private Media? _media;
    private string? _readablePath;
    private bool _updatingPosition;
    private readonly DispatcherTimer _timer;

    public MediaPreviewViewModel(VaultService vault, VaultFile file, Action<string> reportError)
    {
        _vault = vault;
        _path = file.FullPath;
        IsVideo = file.Kind == VaultFileKind.Video;
        _reportError = reportError;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshPlayback();
    }

    public bool IsVideo { get; }
    [ObservableProperty] private MediaPlayer? _player;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _canSeek;
    [ObservableProperty] private bool _isFaulted;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private double _position;
    [ObservableProperty] private int _volume = 80;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private string _timeLabel = "00:00 / 00:00";

    partial void OnPositionChanged(double value)
    {
        if (!_updatingPosition && Player is { IsSeekable: true } player)
            player.Position = (float)Math.Clamp(value, 0, 1);
    }
    partial void OnVolumeChanged(int value) { if (Player is { } player) player.Volume = Math.Clamp(value, 0, 100); }
    partial void OnIsMutedChanged(bool value) { if (Player is { } player) player.Mute = value; }

    [RelayCommand]
    private void TogglePlayback()
    {
        try
        {
            if (_vault.IsPathInLockedSession(_path)) throw new SessionLockedException(_path);
            IsFaulted = false;
            if (Player is null)
            {
                MediaRuntimeService.Initialize();
                _engine = new LibVLC("--no-video-title-show", "--no-metadata-network-access", "--no-snapshot-preview");
                _readablePath = _vault.ResolveReadablePath(_path);
                _media = new Media(_engine, _readablePath, FromType.FromPath);
                // No explorar archivos vecinos ni abrir subtítulos por URL desde metadatos.
                _media.AddOption(":sub-autodetect-file=0");
                Player = new MediaPlayer(_engine) { Media = _media, Volume = Volume, Mute = IsMuted };
                // Dejar que el binding conecte la superficie nativa antes de reproducir.
                var requestedPlayer = Player;
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(Player, requestedPlayer)) StartPlayback();
                }, DispatcherPriority.Loaded);
                return;
            }
            if (Player.IsPlaying) Player.Pause();
            else StartPlayback();
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void StartPlayback()
    {
        if (Player is not { } player) return;
        try
        {
            if (player.State == VLCState.Ended) player.Stop();
            if (!player.Play()) throw new InvalidOperationException("LibVLC no pudo iniciar la reproducción.");
            _timer.Start();
        }
        catch (Exception ex) { Fail(ex); }
    }

    [RelayCommand]
    private void Stop() => Close();

    [RelayCommand] private void ToggleMute() => IsMuted = !IsMuted;

    private void RefreshPlayback()
    {
        if (Player is not { } player) return;
        if (_vault.IsPathInLockedSession(_path)) { Close(); return; }
        if (player.State == VLCState.Error || (player.State == VLCState.Ended && player.Time <= 0))
        {
            Fail(new InvalidOperationException("LibVLC rechazó el archivo o su códec."));
            return;
        }
        IsPlaying = player.IsPlaying;
        CanSeek = player.IsSeekable;
        _updatingPosition = true;
        try { Position = Math.Clamp(player.Position, 0, 1); }
        finally { _updatingPosition = false; }
        TimeLabel = $"{FormatTime(player.Time)} / {FormatTime(player.Length)}";
    }

    internal static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    private void Fail(Exception ex)
    {
        LogService.Warning("No se pudo reproducir el archivo local", ex);
        Close();
        IsFaulted = true;
        ErrorMessage = LocalizationService.Instance["Files_MediaError"];
        _reportError(ErrorMessage);
    }

    /// <summary>Desconecta primero la vista; después libera VLC y elimina el temporal protegido.</summary>
    public void Dispose() { Close(); GC.SuppressFinalize(this); }

    public void Close()
    {
        _timer.Stop();
        var player = Player;
        Player = null;
        player?.Stop();
        player?.Dispose();
        _media?.Dispose();
        _media = null;
        _engine?.Dispose();
        _engine = null;
        if (_readablePath is { } path && path != _path) VaultService.TryCleanupDecryptedTempFile(path);
        _readablePath = null;
        IsPlaying = false;
        CanSeek = false;
        Position = 0;
        TimeLabel = "00:00 / 00:00";
    }
}
