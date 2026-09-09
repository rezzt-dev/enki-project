using System;
using System.Globalization;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Editor de una skill: reutiliza visualmente el editor Markdown (AvaloniaEdit + colorizadores de
/// <c>Views.Editor.Markdown</c>) pero con persistencia propia vía <see cref="SkillService"/>, sin acoplarse
/// a <c>NoteFile</c>/<c>VaultService</c>/sesiones como <c>EditorViewModel</c>.</summary>
public sealed partial class SkillEditorViewModel : ViewModelBase
{
    /// <summary>Mismo debounce de autoguardado que <c>EditorViewModel</c> (docs/ia-skills/
    /// especificacion-desarrollo.md §6).</summary>
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(600);

    private readonly SkillService _skillService;
    private readonly SkillFile _skill;
    private readonly Action _onSaved;
    private readonly NotificationService? _notificationService;
    private readonly DispatcherTimer _saveTimer;
    private string _lastSavedText;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private string _saveStatus = LocalizationService.Instance["Skills_Editor_SaveStatus_Saved"];

    public string Title => _skill.Name;
    public SkillFile Skill => _skill;

    /// <param name="onSaved">Notifica a la tarjeta de la lista (<see cref="SkillViewModel.Refresh"/>) tras
    /// cada guardado, para que un cambio de <c>name</c>/<c>description</c> en el frontmatter se refleje sin
    /// esperar a volver a la lista.</param>
    public SkillEditorViewModel(SkillService skillService, SkillFile skill, Action onSaved, NotificationService? notificationService = null)
    {
        _skillService = skillService;
        _skill = skill;
        _onSaved = onSaved;
        _notificationService = notificationService;
        _text = skill.Content;
        _lastSavedText = skill.Content;

        _saveTimer = new DispatcherTimer { Interval = SaveDebounceDelay };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
    }

    partial void OnTextChanged(string value)
    {
        var dirty = value != _lastSavedText;
        IsDirty = dirty;
        SaveStatus = dirty
            ? LocalizationService.Instance["Skills_Editor_SaveStatus_Unsaved"]
            : LocalizationService.Instance["Skills_Editor_SaveStatus_Saved"];

        _saveTimer.Stop();
        if (dirty) _saveTimer.Start();
    }

    /// <summary>Escribe inmediatamente, saltándose el debounce — llamado al cerrar el editor (volver a la
    /// lista de skills) para no dejar pendiente un cambio que el temporizador nunca llegaría a disparar.</summary>
    public void FlushPendingSave()
    {
        _saveTimer.Stop();
        if (IsDirty) Save();
    }

    /// <summary>Same failure handling as <c>EditorViewModel.Save</c> (docs/analisis-proyecto/roadmap.md,
    /// Fase 1): on an I/O error the skill stays marked dirty and the save timer restarts to retry on the
    /// next tick, instead of the exception escaping uncaught from the debounce timer.</summary>
    [RelayCommand]
    private void Save()
    {
        try
        {
            _skillService.SaveSkill(_skill, Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Error($"No se pudo guardar la skill '{_skill.FilePath}'", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_SkillSaveFailed_Title"),
                LocalizedTextFormatter.Format("Notification_SkillSaveFailed_Message", Title, VaultFailureReasons.DescribeLocalized(ex)),
                NotificationSeverity.Error);

            _saveTimer.Stop();
            _saveTimer.Start();
            return;
        }

        _lastSavedText = Text;
        IsDirty = false;
        SaveStatus = LocalizationService.Instance["Skills_Editor_SaveStatus_Saved"];
        OnPropertyChanged(nameof(Title));
        _onSaved();
    }
}
