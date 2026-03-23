// CRV Trading — SignalR real-time dashboard client
// Dispatches crv:update and crv:alert DOM events consumed by page-level scripts.

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/trading")
    .withAutomaticReconnect()
    .build();

// ── Receive full engine snapshot ──────────────────────────────
connection.on("Update", (data) => {
    _updateNavBar(data.ticker, data.isLive);
    document.dispatchEvent(new CustomEvent("crv:update", { detail: data }));
});

// ── Alert sound (Web Audio API — no file needed) ──────────────
const _alertSounds = {
    ENTRY:   { freq: 880,  dur: 0.12 },  // A5 — bright ding
    EXIT:    { freq: 660,  dur: 0.15 },  // E5 — lower tone
    PARTIAL: { freq: 784,  dur: 0.10 },  // G5
    MOVE_BE: { freq: 784,  dur: 0.10 },  // G5
};
let _audioCtx = null;
function _playDing(type) {
    try {
        if (!_audioCtx) _audioCtx = new (window.AudioContext || window.webkitAudioContext)();
        const s = _alertSounds[type] || { freq: 800, dur: 0.12 };
        const osc  = _audioCtx.createOscillator();
        const gain = _audioCtx.createGain();
        osc.type = "sine";
        osc.frequency.value = s.freq;
        gain.gain.setValueAtTime(0.3, _audioCtx.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.001, _audioCtx.currentTime + s.dur);
        osc.connect(gain).connect(_audioCtx.destination);
        osc.start();
        osc.stop(_audioCtx.currentTime + s.dur);
    } catch (_) { /* silent fallback if audio blocked */ }
}

// ── Receive alert event ────────────────────────────────────────
connection.on("Alert", (alert) => {
    _playDing(alert.type);
    document.dispatchEvent(new CustomEvent("crv:alert", { detail: alert }));
});

// ── Receive completed trade (for Today's Trades table) ────────
connection.on("Trade", (trade) => {
    document.dispatchEvent(new CustomEvent("crv:trade", { detail: trade }));
});

// ── Receive engine status string ──────────────────────────────
connection.on("EngineStatusChanged", (status) => {
    _updateNavBar(null, status);
    document.dispatchEvent(new CustomEvent("crv:status", { detail: status }));
});

// ── Nav bar helper ────────────────────────────────────────────
function _updateNavBar(ticker, status) {
    const tickerEl = document.getElementById("nav-ticker");
    const statusEl = document.getElementById("nav-status");
    if (tickerEl && ticker != null) tickerEl.textContent = ticker;
    if (statusEl && status != null) {
        if (status === "Live")              { statusEl.textContent = "LIVE";           statusEl.className = "badge bg-success"; }
        else if (status === "Session Ended") { statusEl.textContent = "SESSION ENDED"; statusEl.className = "badge bg-warning text-dark"; }
        else if (status === true)           { statusEl.textContent = "LIVE";           statusEl.className = "badge bg-success"; }
        else if (status === false)          { statusEl.textContent = "OFFLINE";        statusEl.className = "badge bg-secondary"; }
        else                                { statusEl.textContent = status;           statusEl.className = "badge bg-secondary"; }
    }
}

// ── Sync badge + last snapshot from REST after connect ────────
function _syncCurrentState() {
    return fetch('/api/engine/status')
        .then(r => r.json())
        .then(d => {
            _updateNavBar(d.snapshot?.ticker ?? null, d.running ? (d.snapshot?.sessionEnded ? "Session Ended" : "Live") : false);
            if (d.snapshot)
                document.dispatchEvent(new CustomEvent("crv:update", { detail: d.snapshot }));
            document.dispatchEvent(new CustomEvent("crv:status", { detail: d.status }));
        })
        .catch(() => {}); // best-effort — don't break the hub on API failure
}

// ── Start connection ──────────────────────────────────────────
connection.start()
    .then(() => { console.log("CRV Hub connected."); return _syncCurrentState(); })
    .catch(err => console.error("CRV Hub error:", err));

// Show OFFLINE immediately when the websocket drops
connection.onclose(() => _updateNavBar(null, "OFFLINE"));

// Re-sync after automatic reconnect so badge reflects live reality
connection.onreconnected(() => _syncCurrentState());
