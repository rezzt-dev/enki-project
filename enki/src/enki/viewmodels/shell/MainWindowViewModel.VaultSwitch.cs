using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using enki.Localization;
using enki.Models;
using enki.Services;

namespace enki.ViewModels;

/// <summary>Cambio de vault en caliente (T4, .docs/project-info/multi-vault-en-caliente.md) — el camino
/// que comparten el selector del sidebar (<see cref="VaultSwitcherViewModel"/>), la paleta de comandos y
/// Ajustes › General › "Cambiar carpeta del vault".
///
/// <para>Está aquí, y no en el viewmodel del selector, por el mismo motivo por el que la composición está
/// en <c>MainWindowViewModel.Composition.cs</c>: cambiar de vault toca <em>todo</em> lo que se instancia
/// allí, y el único sitio con una referencia a todo es este. La recarga en sí no es nueva — es
/// <see cref="OnVaultChanged"/>, la misma que Ajustes ya usaba; lo que añade este archivo es lo que tiene
/// que pasar <b>antes</b> de llamarla para que el cambio sea seguro: volcar lo que esté sin guardar,
/// comprobar que se guardó de verdad, y no dejar nada descifrado del vault que se cierra.</para></summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>Abre <paramref name="path"/> como vault activo sin reiniciar la aplicación.
    ///
    /// <para>El orden importa y es este:</para>
    /// <list type="number">
    /// <item><b>Comprobar la ruta.</b> Una carpeta que ya no está (unidad desconectada, carpeta movida) se
    /// rechaza aquí, con su motivo, antes de haber tocado nada del vault actual.</item>
    /// <item><b>Volcar los guardados pendientes</b> de los editores de notas, de skills y de Ajustes —
    /// todos ellos con autoguardado retardado— y <b>verificar que se guardaron</b>. Si alguno sigue sucio,
    /// el cambio se cancela: el vault que se iba a cerrar todavía tiene texto que solo existe en pantalla,
    /// y cerrarlo lo perdería. Es el mismo criterio de "guardar antes de tocar nada" que ya aplica el
    /// gestor de etiquetas antes de reescribir el frontmatter de medio vault.</item>
    /// <item><b>Bloquear las sesiones cifradas</b> del vault que se deja y barrer sus temporales
    /// descifrados, antes de que <see cref="OnVaultChanged"/> empiece a recargar servicios. Ni una clave
    /// de sesión ni un temporal en claro sobreviven al cambio.</item>
    /// <item><b>Cambiar y recargar</b>, con la sincronización con Polar en pausa (misma
    /// <c>RunWithSyncPausedAsync</c> que Ajustes) para que no haya un ciclo de sync a medias leyendo del
    /// vault viejo y escribiendo en el nuevo.</item>
    /// </list>
    ///
    /// <para>Devuelve <see langword="true"/> solo si el vault activo acabó siendo
    /// <paramref name="path"/>.</para></summary>
    /// <param name="afterAdopt">Se ejecuta entre adoptar la ruta nueva y recargar los módulos, dentro de
    /// la misma pausa de sincronización. Existe por un único caso real, "restaurar copia de seguridad"
    /// (Ajustes › General), que necesita importar las preferencias que venían en el .zip <em>ya</em> sobre
    /// el vault restaurado pero <em>antes</em> de que las pantallas las lean. Sin este hueco, ese flujo
    /// tendría que duplicar la secuencia entera en vez de reutilizarla.</param>
    public async Task<bool> SwitchToVaultAsync(string path, Action? afterAdopt = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var target = Path.GetFullPath(path);
        // Volver a elegir el vault que ya está abierto es un no-op salvo que el llamador traiga trabajo
        // que hacer sobre él (restaurar una copia sobre la misma ruta): en ese caso hay que recargar igual.
        if (afterAdopt is null && VaultService.PathsEqual(target, _vaultService.VaultPath)) return true;

        if (VaultService.DescribeVaultUnavailability(target) is { } reason)
        {
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_VaultSwitchFailed_Title"),
                LocalizedTextFormatter.Format("Notification_VaultSwitchFailed_Message", target, reason),
                NotificationSeverity.Error);
            return false;
        }

        if (!TryFlushEverythingBeforeVaultSwitch()) return false;

        try
        {
            await _polarSyncService.RunWithSyncPausedAsync(() =>
            {
                _vaultService.SetVaultPath(target);
                afterAdopt?.Invoke();
                OnVaultChanged();
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VaultUnavailableException)
        {
            // La carpeta estaba cuando se comprobó y dejó de estar a mitad de camino (se desmontó el
            // disco, se revocó el permiso). No hay a qué volver de forma segura, así que se avisa con el
            // motivo real en vez de dejar la app medio cargada en silencio.
            _notificationService.PushLocalized(
                LocalizedTextFormatter.Format("Notification_VaultSwitchFailed_Title"),
                LocalizedTextFormatter.Format("Notification_VaultSwitchFailed_Message", target,
                    VaultFailureReasons.DescribeLocalized(ex)),
                NotificationSeverity.Error);
            return false;
        }

        VaultSwitcher.Refresh();
        return true;
    }

    /// <summary>Vuelca todo lo que tenga autoguardado retardado y responde si de verdad quedó guardado.
    /// Un fallo de guardado (disco lleno, permisos, sesión bloqueada) deja el editor
    /// <see cref="EditorViewModel.IsDirty"/> en true, que es justo lo que se comprueba: no se confía en
    /// que "llamé a guardar" signifique "se guardó".</summary>
    private bool TryFlushEverythingBeforeVaultSwitch()
    {
        Settings?.FlushPendingSave();
        Skills?.ActiveEditor?.FlushPendingSave();
        Notes?.FlushAllEditors();

        var unsaved = Notes?.OpenEditors.Where(editor => editor.IsDirty).Select(editor => editor.Title).ToList() ?? [];
        if (Skills?.ActiveEditor is { IsDirty: true } skillEditor) unsaved.Add(skillEditor.Title);
        if (unsaved.Count == 0) return true;

        _notificationService.PushLocalized(
            LocalizedTextFormatter.Format("Notification_VaultSwitchBlocked_Title"),
            LocalizedTextFormatter.Format("Notification_VaultSwitchBlocked_Message", string.Join(", ", unsaved)),
            NotificationSeverity.Error);
        return false;
    }

    /// <summary>Diálogo de carpeta para "abrir otra carpeta como vault…", con la misma resolución de
    /// "la carpeta elegida es un contenedor, no el vault" que <see cref="VaultSetupViewModel"/> y Ajustes
    /// (ver <see cref="VaultService.ResolveVaultFolder"/>). Devuelve null si se cancela.</summary>
    private async Task<string?> PickVaultFolderAsync()
    {
        var topLevel = App.MainWindowRef is not null ? TopLevel.GetTopLevel(App.MainWindowRef) : null;
        if (topLevel?.StorageProvider is null) return null;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Instance["Settings_General_ChangeVaultFolder_DialogTitle"],
            AllowMultiple = false
        });

        var picked = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        return string.IsNullOrWhiteSpace(picked) ? null : VaultService.ResolveVaultFolder(picked);
    }

    /// <summary>Abre el selector de vaults desde la paleta de comandos — el equivalente por teclado de
    /// pulsar el rótulo del vault en la cabecera del sidebar.</summary>
    private void OpenVaultSwitcher() => VaultSwitcher.IsOpen = true;
}
