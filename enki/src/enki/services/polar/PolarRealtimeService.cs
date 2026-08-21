using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Supabase.Realtime;
using Supabase.Realtime.Interfaces;
using Supabase.Realtime.PostgresChanges;

namespace enki.Services;

/// <summary>Wraps the official <c>Supabase.Realtime</c> client so <see cref="PolarSyncService"/> fires the
/// instant a remote device changes a task_lists/tasks/subtasks/reminders row, instead of waiting for
/// <c>MainWindowViewModel</c>'s polling timer. Purely a latency optimization: the postgres_changes handler
/// below never inspects the payload, it just debounces and calls
/// <see cref="PolarSyncService.SyncNowAsync"/> — the existing push/pull/LWW logic is untouched. Any failure
/// to connect or subscribe (offline, a table not yet added to the <c>supabase_realtime</c> publication,
/// etc.) is swallowed silently: the polling timer already covers correctness, so this service only ever
/// makes sync feel faster, never more correct.</summary>
public sealed class PolarRealtimeService
{
    private static readonly string[] SyncedTables = ["task_lists", "tasks", "subtasks", "reminders"];
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);

    private readonly PolarAuthService _auth;
    private readonly PolarSyncService _sync;
    private readonly DispatcherTimer _debounceTimer;

    private Client? _client;
    private string? _appliedAccessToken;
    private bool _connecting;

    /// <summary>True once <see cref="ConnectAsync"/> has established and is still holding an open socket —
    /// false the instant the socket closes or errors out on its own, not just after an explicit
    /// <see cref="Disconnect"/>. <c>MainWindowViewModel</c>'s polling-sync timer checks this on every tick
    /// (Fase 9, docs/analisis-proyecto/roadmap.md) and calls <see cref="ConnectAsync"/> again when it's
    /// false, so a socket dropped by an idle timeout, sleep/wake, or a network blip that doesn't fully take
    /// the device offline gets retried on the same cadence as the offline-first sync backoff — instead of
    /// staying dark until the next full unlink/relink cycle or app restart, which is all that reconnected it
    /// before.</summary>
    public bool IsConnected => _client is not null;

    public PolarRealtimeService(PolarAuthService auth, PolarSyncService sync)
    {
        _auth = auth;
        _sync = sync;
        _debounceTimer = new DispatcherTimer { Interval = DebounceInterval };
        _debounceTimer.Tick += async (_, _) =>
        {
            _debounceTimer.Stop();
            await _sync.SyncNowAsync();
        };
    }

    /// <summary>No-op when already connected/connecting, unlinked, or <c>local.properties</c> is missing —
    /// same "just stay disabled" tolerance as the rest of the Polar-linking surface.</summary>
    public async Task ConnectAsync()
    {
        if (IsConnected || _connecting || !SupabaseConfig.IsConfigured || string.IsNullOrEmpty(_auth.AccessToken))
            return;

        _connecting = true;
        Client? client = null;
        try
        {
            var wsUrl = SupabaseConfig.Url!
                .Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                .Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase) + "/realtime/v1";

            var options = new ClientOptions();
            options.Parameters.ApiKey = SupabaseConfig.AnonKey;
            options.Parameters.Token = SupabaseConfig.AnonKey;

            client = new Client(wsUrl, options);
            await client.ConnectAsync();
            client.SetAuth(_auth.AccessToken);
            _appliedAccessToken = _auth.AccessToken;
            client.AddStateChangedHandler(HandleSocketStateChanged);

            var channel = client.Channel("polar-sync");

            // Payload ignored on purpose — see class remarks, this is only ever a "something changed, go
            // sync" signal, never a data path of its own.
            foreach (var table in SyncedTables)
                channel.OnPostgresChange((_, _) => RestartDebounce(), PostgresChangesOptions.ListenType.All, new PostgresChangesFilter { Table = table });

            await channel.Subscribe();
            _client = client;
        }
        catch
        {
            try { client?.Disconnect(); } catch { /* best-effort cleanup of a half-open socket */ }
            _client = null;
            _appliedAccessToken = null;
        }
        finally
        {
            _connecting = false;
        }
    }

    public void Disconnect()
    {
        _debounceTimer.Stop();
        if (_client is { } client)
        {
            try { client.RemoveStateChangedHandler(HandleSocketStateChanged); } catch { /* already gone/broken */ }
            try { client.Disconnect(); } catch { /* already gone/broken, nothing to clean up */ }
        }
        _client = null;
        _appliedAccessToken = null;
    }

    /// <summary>Fires on the socket's own background thread whenever its state changes — marshaled onto the
    /// UI thread before touching <see cref="_client"/>, since every other mutation of that field
    /// (<see cref="ConnectAsync"/>/<see cref="Disconnect"/>) already happens there. Only <c>Close</c>/
    /// <c>Error</c> matter here: they're what "the socket is no longer usable" looks like from this API, so
    /// clearing <see cref="_client"/> on either one is what makes <see cref="IsConnected"/> go false and lets
    /// the next poll of the sync timer call <see cref="ConnectAsync"/> again instead of the stale guard at
    /// its top treating a dead socket as still connected forever.</summary>
    private void HandleSocketStateChanged(IRealtimeClient<RealtimeSocket, RealtimeChannel> sender, Supabase.Realtime.Constants.SocketState state)
    {
        if (state is not (Supabase.Realtime.Constants.SocketState.Close or Supabase.Realtime.Constants.SocketState.Error))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_client, sender)) return; // stale handler from an already-replaced client
            _debounceTimer.Stop();
            _client = null;
            _appliedAccessToken = null;
        });
    }

    /// <summary>Called after every <see cref="PolarSyncService.SyncCompleted"/> tick, since that's the only
    /// place a silent <see cref="PolarAuthService.EnsureFreshTokenAsync"/> token refresh is observable from
    /// — re-applies the JWT to the socket only when it actually changed, since
    /// <see cref="Client.SetAuth"/> pushes a message to the server for every already-joined channel.</summary>
    public void RefreshAuthIfNeeded()
    {
        if (_client is null) return;

        var token = _auth.AccessToken;
        if (string.IsNullOrEmpty(token) || token == _appliedAccessToken) return;

        _client.SetAuth(token);
        _appliedAccessToken = token;
    }

    private void RestartDebounce()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }
}
