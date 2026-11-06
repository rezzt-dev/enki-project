using System;
using System.Collections.ObjectModel;
using System.IO;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Qué hace esta pestaña cuando la nota que tiene abierta cambia fuera de Enki — la política, no
/// la detección: quien detecta es <see cref="VaultChangeWatcher"/> (vigilancia activa) y
/// <c>VaultService.WriteContentChecked</c> (comprobación previa a guardar). Ver
/// <c>.docs/project-info/cambios-externos-en-el-vault.md</c>.
///
/// <para><b>La regla.</b> Sin cambios locales sin guardar, la nota se recarga sola y se avisa sin
/// interrumpir. Con cambios locales sin guardar, el autoguardado de <em>esta</em> nota se pausa y se
/// pregunta: conservar la mía, cargar la de disco o ver las diferencias. Ninguna de las dos opciones
/// destruye nada — la versión descartada se guarda antes en el historial local
/// (<see cref="NoteVersionTrigger.ExternalConflict"/>), así que siempre se puede volver.</para>
///
/// <para><b>Decidir luego.</b> Cerrar el diálogo sin elegir no reanuda el autoguardado: el texto sigue
/// siendo editable, pero no se escribe a disco hasta que haya decisión. La cabecera muestra mientras
/// tanto un aviso que vuelve a abrir el diálogo, para que la pausa nunca sea un estado invisible.</para>
///
/// <para><b>Sesiones protegidas.</b> Una nota de una sesión bloqueada no se lee ni se compara; el
/// vigilante aplaza la revisión y la retoma al desbloquearse. Si aun así llegase aquí, se trata como un
/// fallo de guardado normal y la pestaña no se queda en un estado a medias.</para></summary>
public sealed partial class EditorViewModel
{
    /// <summary>Huella de lo último que esta pestaña leyó o escribió en disco. Es la referencia contra la
    /// que se decide si un cambio es propio o ajeno, y la que viaja a
    /// <c>VaultService.WriteContentChecked</c> en cada guardado. <see langword="null"/> mientras no se haya
    /// podido leer (nota recién borrada, archivo ilegible), en cuyo caso el guardado se comporta como
    /// siempre: sin comprobación previa, porque no hay nada con lo que comparar.</summary>
    private NoteDiskStamp? _diskStamp;

    /// <summary>La versión de disco que abrió el conflicto, entera: metadatos, contenido y huella, tal y
    /// como se leyeron de una vez. Se conserva mientras la decisión siga pendiente, para poder reabrir el
    /// diálogo sin volver a leer — y sin arriesgarse a leer un tercer contenido distinto a mitad de
    /// decisión, ni a mezclar ese contenido con unos metadatos leídos en otro momento.</summary>
    private (NoteFile Note, string Content, NoteDiskStamp Stamp)? _conflicting;

    /// <summary>Autoguardado en pausa por un cambio externo sin resolver. Lo consultan
    /// <c>OnTextChanged</c>, <c>FlushPendingSave</c> y <c>Save</c> en <c>EditorViewModel.cs</c>.</summary>
    [ObservableProperty]
    private bool _isAutosavePaused;

    partial void OnIsAutosavePausedChanged(bool value) => OnPropertyChanged(nameof(ShowExternalConflictBadge));

    /// <summary>Diálogo de conflicto visible. Puede cerrarse ("decidir luego") sin que
    /// <see cref="IsAutosavePaused"/> deje de ser cierto.</summary>
    [ObservableProperty]
    private bool _isExternalConflictOpen;

    /// <summary>La nota abierta ya no existe en disco. El buffer se conserva en pantalla y se ofrece
    /// guardarlo como nota nueva; hasta entonces el autoguardado sigue en pausa, porque escribir
    /// recrearía en silencio un archivo que alguien borró a propósito.</summary>
    [ObservableProperty]
    private bool _isExternallyDeleted;

    [ObservableProperty]
    private bool _isExternalDiffVisible;

    /// <summary>Aviso en la cabecera mientras haya una decisión pendiente — la pausa del autoguardado no
    /// puede ser un estado invisible.</summary>
    public bool ShowExternalConflictBadge => IsAutosavePaused && !IsExternalConflictOpen;

    /// <summary>Comparación línea a línea entre la versión de disco y la del editor, con el mismo
    /// <see cref="TextDiffService"/> y la misma plantilla visual que el historial de versiones: lo
    /// marcado como añadido es lo que aporta la versión del editor; lo marcado como eliminado, lo que
    /// solo está en la de disco.</summary>
    public ObservableCollection<DiffLine> ExternalDiffLines { get; } = [];

    public string ExternalConflictMessage =>
        string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Editor_External_Conflict_Message"], Title);

    public string ExternalDeletedMessage =>
        string.Format(LocalizationService.Instance.CurrentCulture,
            LocalizationService.Instance["Editor_External_Deleted_Message"], Title);

    public string ExternalDiffToggleLabel => LocalizationService.Instance[
        IsExternalDiffVisible ? "Editor_External_Conflict_HideDiff" : "Editor_External_Conflict_ShowDiff"];

    partial void OnIsExternalDiffVisibleChanged(bool value) => OnPropertyChanged(nameof(ExternalDiffToggleLabel));

    partial void OnIsExternalConflictOpenChanged(bool value) => OnPropertyChanged(nameof(ShowExternalConflictBadge));

    // ===================== Registro en el vigilante =====================

    /// <summary>Toma la huella de la nota tal y como está en disco ahora y registra la ruta en el
    /// vigilante. Se llama al abrir la pestaña y cada vez que la nota cambia de ruta por un renombrado o
    /// movimiento hecho desde la propia Enki.</summary>
    private void TrackForExternalChanges()
    {
        if (_changeWatcher is null) return;

        if (_diskStamp is { } stamp) _changeWatcher.Track(Note.FullPath, stamp);
        _changeWatcher.ExternalChangeDetected -= OnExternalChangeDetected;
        _changeWatcher.ExternalChangeDetected += OnExternalChangeDetected;
    }

    /// <summary>Adopta la huella de lo que acaba de quedar en disco, en la pestaña y en el vigilante — es
    /// el paso que hace que una escritura de Enki no se confunda después con una de fuera.</summary>
    private void AdoptDiskStamp(NoteDiskStamp? stamp)
    {
        _diskStamp = stamp;
        if (stamp is { } value) _changeWatcher?.UpdateStamp(Note.FullPath, value);
    }

    /// <summary>Llega en un hilo del pool (ver <see cref="VaultChangeWatcher"/>), así que se filtra por
    /// ruta ahí mismo —barato y sin tocar nada enlazado— y solo lo que es de esta pestaña se marshala al
    /// hilo de interfaz.</summary>
    private void OnExternalChangeDetected(VaultExternalChange change)
    {
        if (!VaultService.PathsEqual(change.FullPath, Note.FullPath)) return;
        Dispatcher.UIThread.Post(() => HandleExternalChange(change));
    }

    // ===================== Política =====================

    /// <summary>Punto único de entrada de un cambio externo sobre esta nota, venga del vigilante o de la
    /// comprobación previa a guardar. Debe llamarse en el hilo de interfaz.</summary>
    /// <param name="pendingText">El texto que el guardado no llegó a escribir, cuando quien avisa es la
    /// comprobación previa a guardar: se conserva como pendiente para que no se pierda por el camino.</param>
    public void HandleExternalChange(VaultExternalChange change, string? pendingText = null)
    {
        if (pendingText is not null)
        {
            _pendingText = pendingText;
            IsDirty = pendingText != _lastSavedContent;
        }

        switch (change.Kind)
        {
            case VaultExternalChangeKind.Renamed when change.NewFullPath is { } newPath:
                HandleRenamedExternally(newPath);
                break;
            case VaultExternalChangeKind.Deleted:
                HandleDeletedExternally();
                break;
            default:
                HandleModifiedExternally();
                break;
        }
    }

    private void HandleModifiedExternally()
    {
        (NoteFile Note, string Content, NoteDiskStamp Stamp)? disk;
        try
        {
            disk = _vaultService.ReadFromDisk(Note.FullPath);
        }
        catch (SessionLockedException)
        {
            // La sesión se bloqueó entre la detección y este momento. No se lee nada: el vigilante lo
            // reintentará al desbloquearla, y hasta entonces el autoguardado ya falla por su cuenta con
            // el aviso de sesión bloqueada de siempre.
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Warning($"No se pudo leer la versión de disco de '{Note.FullPath}'", ex);
            return;
        }

        if (disk is not { } fresh)
        {
            HandleDeletedExternally();
            return;
        }

        // Puede que lo de fuera coincida con lo que ya hay en pantalla (el propio usuario copiando el
        // mismo texto, un sincronizador devolviendo una versión idéntica): no hay nada que resolver, solo
        // que adoptar la huella nueva para no volver a mirar.
        if (string.Equals(fresh.Content, Document.Text, StringComparison.Ordinal))
        {
            Note = fresh.Note;
            AdoptDiskStamp(fresh.Stamp);
            _lastSavedContent = fresh.Content;
            IsDirty = false;
            return;
        }

        var autoReload = _settingsService?.Settings.VaultAutoReloadExternalChanges ?? true;
        if (!IsDirty && autoReload)
        {
            ReloadSilently(fresh);
            return;
        }

        OpenConflict(fresh);
    }

    /// <summary>Recarga sin preguntar: no hay nada local que perder. Conserva la posición del cursor (y
    /// con ella el desplazamiento, porque la vista lleva el cursor a la vista al colocarlo) acotada al
    /// tamaño del texto nuevo, que puede ser más corto.</summary>
    private void ReloadSilently((NoteFile Note, string Content, NoteDiskStamp Stamp) fresh)
    {
        var caret = LastCaretOffset;
        ApplyExternalContent(fresh);
        RequestCaretAt(Math.Min(caret, Document.TextLength));

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteReloadedExternally_Title"),
            LocalizedTextFormatter.Format("Notification_NoteReloadedExternally_Message", Title),
            NotificationSeverity.Info);
    }

    private void OpenConflict((NoteFile Note, string Content, NoteDiskStamp Stamp) fresh)
    {
        _conflicting = fresh;
        _saveTimer.Stop();
        IsAutosavePaused = true;
        IsExternalDiffVisible = false;
        ExternalDiffLines.Clear();
        OnPropertyChanged(nameof(ExternalConflictMessage));
        IsExternalConflictOpen = true;

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteExternalConflict_Title"),
            LocalizedTextFormatter.Format("Notification_NoteExternalConflict_Message", Title),
            NotificationSeverity.Warning);
    }

    /// <summary>La nota se renombró o se movió fuera de Enki y se sabe a dónde: la pestaña sigue a su
    /// archivo en vez de quedarse apuntando a una ruta que ya no existe. Si además el contenido cambió,
    /// la comparación de después lo trata como cualquier otro cambio externo.</summary>
    private void HandleRenamedExternally(string newFullPath)
    {
        var moved = _vaultService.GetNote(newFullPath);
        if (moved is null)
        {
            HandleDeletedExternally();
            return;
        }

        SyncRenamedNote(moved);
        _onChanged();

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteRenamedExternally_Title"),
            LocalizedTextFormatter.Format("Notification_NoteRenamedExternally_Message", Title),
            NotificationSeverity.Info);

        HandleModifiedExternally();
    }

    private void HandleDeletedExternally()
    {
        if (IsExternallyDeleted) return;

        _saveTimer.Stop();
        _diskStamp = null;
        _changeWatcher?.Untrack(Note.FullPath);
        IsAutosavePaused = true;
        IsExternalConflictOpen = false;
        OnPropertyChanged(nameof(ExternalDeletedMessage));
        IsExternallyDeleted = true;
        _onChanged();

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteDeletedExternally_Title"),
            LocalizedTextFormatter.Format("Notification_NoteDeletedExternally_Message", Title),
            NotificationSeverity.Warning);
    }

    // ===================== Acciones del diálogo =====================

    /// <summary>"Conservar la mía": guarda antes la versión de disco en el historial —para que descartarla
    /// no sea perderla— y escribe el buffer encima.</summary>
    [RelayCommand]
    private void KeepMine()
    {
        if (_conflicting is not { } theirs) return;

        if (!TryPreserveConflictSnapshot(theirs.Content)) return;
        // Adoptar la huella de la versión externa es lo que deja pasar la comprobación previa del
        // guardado de abajo: ya se ha visto ese contenido y se ha decidido descartarlo a conciencia.
        AdoptDiskStamp(theirs.Stamp);
        ResolveConflict();

        Save(Document.Text);
        History.Reload();
        if (IsDirty || IsAutosavePaused) return;

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteConflictKeptMine_Title"),
            LocalizedTextFormatter.Format("Notification_NoteConflictKeptMine_Message", Title),
            NotificationSeverity.Success);
    }

    /// <summary>"Cargar la de disco": guarda antes el buffer del editor en el historial y adopta el
    /// contenido externo.</summary>
    [RelayCommand]
    private void LoadFromDisk()
    {
        if (_conflicting is not { } theirs) return;

        if (!TryPreserveConflictSnapshot(Document.Text)) return;

        var caret = LastCaretOffset;
        ResolveConflict();
        ApplyExternalContent(theirs);
        RequestCaretAt(Math.Min(caret, Document.TextLength));
        History.Reload();

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteConflictLoadedFromDisk_Title"),
            LocalizedTextFormatter.Format("Notification_NoteConflictLoadedFromDisk_Message", Title),
            NotificationSeverity.Success);
    }

    [RelayCommand]
    private void ToggleExternalDiff()
    {
        if (IsExternalDiffVisible)
        {
            IsExternalDiffVisible = false;
            ExternalDiffLines.Clear();
            return;
        }

        ExternalDiffLines.Clear();
        foreach (var line in TextDiffService.Compare(_conflicting?.Content ?? string.Empty, Document.Text))
            ExternalDiffLines.Add(line);
        IsExternalDiffVisible = true;
    }

    /// <summary>"Decidir luego" (y <c>Esc</c>): cierra el diálogo y deja el autoguardado en pausa. No es
    /// una tercera opción silenciosa — el aviso de la cabecera queda visible y vuelve a abrir esto.</summary>
    [RelayCommand]
    private void PostponeExternalDecision()
    {
        IsExternalConflictOpen = false;
        IsExternalDiffVisible = false;
        ExternalDiffLines.Clear();
    }

    [RelayCommand]
    private void ReopenExternalConflict()
    {
        if (IsExternallyDeleted || _conflicting is null) return;
        OnPropertyChanged(nameof(ExternalConflictMessage));
        IsExternalConflictOpen = true;
    }

    /// <summary>"Guardar como nota nueva" tras un borrado desde fuera: recrea el archivo con el contenido
    /// que seguía en pantalla. Al estar la ruta libre, <see cref="VaultService.CreateNote"/> la reutiliza
    /// tal cual; si algo la hubiera ocupado entretanto, nace con sufijo numérico y la pestaña sigue a la
    /// nota real, nunca a la ruta que el usuario creía.</summary>
    [RelayCommand]
    private void SaveAfterExternalDelete()
    {
        var folder = Path.GetDirectoryName(Note.FullPath);
        NoteFile recreated;
        try
        {
            recreated = _vaultService.CreateNote(Title, folder, Document.Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            LogService.Error($"No se pudo recrear la nota borrada desde fuera '{Note.FullPath}'", ex);
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NoteSaveFailed_Title"),
                LocalizedTextFormatter.Format("Notification_NoteSaveFailed_Message", Title, VaultFailureReasons.DescribeLocalized(ex)),
                NotificationSeverity.Error);
            return;
        }

        SyncRenamedNote(recreated);
        AdoptDiskStamp(NoteDiskStamps.TryRead(recreated.FullPath));
        TrackForExternalChanges();
        _lastSavedContent = Document.Text;
        _pendingText = null;
        IsDirty = false;
        _lastSavedUtc = DateTime.UtcNow;
        SaveStatus = FormatSaveStatus(_lastSavedUtc);

        IsExternallyDeleted = false;
        IsAutosavePaused = false;
        _onChanged();

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteRecreatedAfterDelete_Title"),
            LocalizedTextFormatter.Format("Notification_NoteRecreatedAfterDelete_Message", Title),
            NotificationSeverity.Success);
    }

    /// <summary>Acepta el borrado: la pestaña se cierra y el contenido que hubiera en pantalla se deja
    /// ir. Es la única acción del diálogo de borrado que descarta algo, y por eso está como salida
    /// secundaria frente a "guardar como nota nueva".</summary>
    [RelayCommand]
    private void DiscardAfterExternalDelete()
    {
        IsExternallyDeleted = false;
        IsAutosavePaused = false;
        _pendingText = null;
        IsDirty = false;
        _onBack();
    }

    // ===================== Auxiliares =====================

    private void ResolveConflict()
    {
        _conflicting = null;
        IsExternalConflictOpen = false;
        IsExternalDiffVisible = false;
        ExternalDiffLines.Clear();
        IsAutosavePaused = false;
    }

    /// <summary>Lleva a la pestaña el contenido que hay ahora en disco y reconcilia todo lo que se deriva
    /// de él — indicadores de guardado, etiquetas, enlaces, estadísticas y destinos de wikilink, el mismo
    /// conjunto que refresca una restauración del historial. Avisa por
    /// <see cref="VaultService.ApplyExternalChange"/> en vez de llamar al callback de cambio de esta
    /// pestaña: así las pantallas que listan notas se enteran sin que el editor las conozca.</summary>
    private void ApplyExternalContent((NoteFile Note, string Content, NoteDiskStamp Stamp) fresh)
    {
        _saveTimer.Stop();
        _pendingText = null;
        Document.Text = fresh.Content;
        _lastSavedContent = fresh.Content;
        IsDirty = false;
        _lastSavedUtc = DateTime.UtcNow;
        SaveStatus = FormatSaveStatus(_lastSavedUtc);

        Note = fresh.Note;
        AdoptDiskStamp(fresh.Stamp);
        TagsInput = string.Join(", ", Note.Tags);
        RefreshWikiLinkScopeNotes();
        Backlinks.Refresh(Note);
        Stats.Refresh();
        History.Reload();
        RefreshDailyNoteState();

        _vaultService.ApplyExternalChange(Note.FullPath);
    }

    /// <summary>Red de seguridad de última hora: si la pestaña se cierra (o la app), con una decisión de
    /// conflicto aún pendiente y texto sin guardar, ese texto no se puede escribir sobre la nota —es
    /// justo lo que la pausa impide— pero tampoco se puede perder. Se guarda como versión del historial
    /// con el motivo "conflicto externo", que es exactamente donde el usuario ya sabe buscarlo.</summary>
    private bool PreserveUnsavedTextOnClose()
    {
        var unsaved = _pendingText ?? Document.Text;
        if (!TryPreserveConflictSnapshot(unsaved)) return false;

        _notificationService?.PushLocalized(
            LocalizedTextFormatter.Format("Notification_NoteUnsavedKeptInHistory_Title"),
            LocalizedTextFormatter.Format("Notification_NoteUnsavedKeptInHistory_Message", Title),
            NotificationSeverity.Warning);
        return true;
    }

    private bool TryPreserveConflictSnapshot(string content)
    {
        try
        {
            _vaultService.CaptureSnapshotChecked(Note, content, NoteVersionTrigger.ExternalConflict);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SessionLockedException)
        {
            _notificationService?.PushLocalized(
                LocalizedTextFormatter.Format("Notification_NoteSaveFailed_Title"),
                LocalizedTextFormatter.Format("Notification_NoteSaveFailed_Message", Title,
                    VaultFailureReasons.DescribeLocalized(ex)), NotificationSeverity.Error);
            return false;
        }
    }

    /// <summary>Deja de atender avisos del vigilante — lo llama <c>Close</c> al cerrar la pestaña.</summary>
    private void UnsubscribeFromExternalChanges()
    {
        if (_changeWatcher is not null) _changeWatcher.ExternalChangeDetected -= OnExternalChangeDetected;
    }

    /// <summary>Reetiqueta los textos que esta parte del editor compone en C# cuando cambia el idioma en
    /// caliente — lo llama <c>RefreshLocalizedLabels</c> en <c>EditorViewModel.cs</c>.</summary>
    private void RefreshExternalChangeLabels()
    {
        OnPropertyChanged(nameof(ExternalConflictMessage));
        OnPropertyChanged(nameof(ExternalDeletedMessage));
        OnPropertyChanged(nameof(ExternalDiffToggleLabel));
    }
}
