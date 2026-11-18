using System;
using System.Threading.Tasks;
using UnityEngine;

namespace WhatIf
{
    /// <summary>
    /// Server status in the HUD: checks /health every few seconds and says so right away when the
    /// server can't be reached (and which address was tried) or is still loading its models, instead
    /// of requests silently going nowhere. Only changes are shown, so the HUD isn't flooded.
    /// </summary>
    public partial class WhatIfController
    {
        enum ServerState { Unknown, Unreachable, Loading, Failed, Ready }

        ServerState _serverState = ServerState.Unknown;
        bool _watching;

        /// <summary>True when the last check found the server up and ready.</summary>
        bool ServerReady => _serverState == ServerState.Ready;

        async void WatchServer()
        {
            if (_watching) return;
            _watching = true;
            while (this != null && isActiveAndEnabled)
            {
                var state = ServerState.Unreachable;
                string loading = null;
                try
                {
                    var h = await _client.Health();
                    if (h.Models == "failed") state = ServerState.Failed;
                    else if (!h.Ready) { state = ServerState.Loading; loading = string.Join(", ", h.Loading); }
                    else state = ServerState.Ready;
                }
                catch (Exception) { /* unreachable: shown below */ }
                if (this == null) return;
                if (state != _serverState) ShowServerState(state, loading);
                // check more often while something is wrong, so recovery is shown quickly
                await Task.Delay(TimeSpan.FromSeconds(state == ServerState.Ready ? 10 : 3));
            }
            _watching = false;
        }

        void ShowServerState(ServerState state, string loading)
        {
            var previous = _serverState;
            _serverState = state;
            Debug.Log($"[WhatIf] server {_client.BaseUrl}: {state}" + (loading != null ? $" ({loading})" : ""));
            switch (state)
            {
                case ServerState.Unreachable:
                    hud?.Show("Server not reachable", _client.BaseUrl + " - retrying. Same network? Server running?", sticky: true);
                    break;
                case ServerState.Loading:
                    hud?.Show("Server is starting", $"Starting {loading} - ready in a moment.", sticky: true);
                    break;
                case ServerState.Failed:
                    hud?.Show("Server problem", "Its models failed to load - see the server log.", sticky: true);
                    break;
                case ServerState.Ready:
                    // only worth a word after a problem; at start the normal hint is shown
                    if (previous == ServerState.Unreachable || previous == ServerState.Loading || previous == ServerState.Failed)
                    {
                        hud?.Show("Server connected", null);
                        if (!HasScene() && !_busy) ShowPreset();
                    }
                    break;
            }
        }
    }
}
