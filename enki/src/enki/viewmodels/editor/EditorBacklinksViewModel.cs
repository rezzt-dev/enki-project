using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Panel «enlaces» del editor: enlaces entrantes (<see cref="Items"/>), notas relacionadas por
/// etiquetas y enlaces compartidos (<see cref="Related"/>) y menciones sin enlazar
/// (<see cref="Mentions"/>). Los tres bloques salen del mismo barrido del vault.</summary>
public sealed partial class EditorBacklinksViewModel : ObservableObject
{
    private readonly VaultService _vaultService;
    private readonly Action<NoteFile> _onOpenNote;
    private readonly Action<string>? _insertWikiLink;
    private readonly NotificationService? _notificationService;
    private NoteFile _currentNote;

    public ObservableCollection<EditorBacklink> Items { get; } = [];
    public ObservableCollection<RelatedNote> Related { get; } = [];
    public ObservableCollection<UnlinkedMention> Mentions { get; } = [];

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _hasRelated;

    [ObservableProperty]
    private bool _hasMentions;

    public EditorBacklinksViewModel(NoteFile note, VaultService vaultService, Action<NoteFile> onOpenNote,
        Action<string>? insertWikiLink = null, NotificationService? notificationService = null)
    {
        _vaultService = vaultService;
        _onOpenNote = onOpenNote;
        _insertWikiLink = insertWikiLink;
        _notificationService = notificationService;
        _currentNote = note;
        Refresh(note);
    }

    /// <summary>Recomputes <see cref="Items"/> against <paramref name="currentNote"/> — the caller
    /// (<c>ViewModels.EditorViewModel</c>) always passes its own, current <c>Note</c>, since resolution now
    /// matches by path (see <see cref="EditorBacklinksService.FindBacklinks"/>) and a stale path from before
    /// a rename would never match any note the fresh vault scan below finds
    /// (docs/analisis-proyecto/roadmap.md, Fase 5). Las notas relacionadas y las menciones salen de ese
    /// mismo barrido, sin releer el vault.</summary>
    public void Refresh(NoteFile currentNote)
    {
        _currentNote = currentNote;
        var allNotesWithContent = _vaultService.GetAllNotesAcrossVaultWithContent();
        var backlinks = EditorBacklinksService.FindBacklinks(allNotesWithContent, currentNote);

        Items.Clear();
        foreach (var link in backlinks) Items.Add(link);
        IsEmpty = Items.Count == 0;

        Replace(Related, NoteRelationsService.FindRelated(allNotesWithContent, currentNote));
        HasRelated = Related.Count > 0;
        Replace(Mentions, NoteRelationsService.FindUnlinkedMentions(allNotesWithContent, currentNote));
        HasMentions = Mentions.Count > 0;
    }

    private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    [RelayCommand]
    private void Open(EditorBacklink backlink)
    {
        var note = _vaultService.GetNote(backlink.FullPath);
        if (note is not null) _onOpenNote(note);
    }

    [RelayCommand]
    private void OpenRelated(RelatedNote related)
    {
        var note = _vaultService.GetNote(related.FullPath);
        if (note is not null) _onOpenNote(note);
    }

    [RelayCommand]
    private void OpenMention(UnlinkedMention mention)
    {
        var note = _vaultService.GetNote(mention.FullPath);
        if (note is not null) _onOpenNote(note);
    }

    /// <summary>Añade al final de la nota activa un wikilink a la nota relacionada. Pasa por el documento
    /// del editor, así que el guardado, el historial y la detección de conflictos son los de siempre.</summary>
    [RelayCommand]
    private void LinkRelated(RelatedNote related)
    {
        if (_insertWikiLink is null || related.LinkName is null) return;
        _insertWikiLink(related.LinkName);
        Related.Remove(related);
        HasRelated = Related.Count > 0;
    }

    /// <summary>Convierte la mención en <c>[[enlace]]</c> dentro de la otra nota. La escritura es
    /// comprobada: si la nota cambió fuera de Enki desde que se leyó, no se toca y se avisa.</summary>
    [RelayCommand]
    private void LinkMention(UnlinkedMention mention)
    {
        try
        {
            var changed = _vaultService.LinkMention(mention, _currentNote);
            if (changed)
            {
                Push("Notification_MentionLinked_Message", NotificationSeverity.Success, mention.Title);
            }
            else
            {
                Push("Notification_MentionLinkOutdated_Message", NotificationSeverity.Info, mention.Title);
            }
        }
        catch (ExternalModificationException)
        {
            Push("Notification_MentionLinkConflict_Message", NotificationSeverity.Warning, mention.Title);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error($"No se pudo enlazar la mención en '{mention.FullPath}'", ex);
            Push("Notification_MentionLinkFailed_Message", NotificationSeverity.Error, mention.Title);
        }

        Refresh(_currentNote);
    }

    private void Push(string messageKey, NotificationSeverity severity, string noteTitle) =>
        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_MentionLink_Title"),
            LocalizedTextFormatter.Format(messageKey, noteTitle),
            severity);
}
