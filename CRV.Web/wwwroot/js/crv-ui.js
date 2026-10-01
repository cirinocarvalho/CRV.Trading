// CRV Trading — shared UI: confirm dialog, toasts, and the engine bar.
// Loaded on every page before crv-hub.js, which feeds the engine bar via window.CRV.engine.
(function () {
    'use strict';
    const CRV = window.CRV = window.CRV || {};

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }
    CRV.esc = esc;

    // ── Confirm dialog ───────────────────────────────────────────
    // CRV.confirm({ title, html, ok, okClass }) → Promise<boolean>. `html` must already be escaped.
    let modal = null;
    CRV.confirm = function ({ title, html, ok = 'Confirm', okClass = 'btn-warning' }) {
        const el = document.getElementById('crv-confirm');
        if (!el || !window.bootstrap) return Promise.resolve(window.confirm(title));
        modal = modal || new bootstrap.Modal(el);
        document.getElementById('crv-confirm-title').textContent = title;
        document.getElementById('crv-confirm-body').innerHTML = html;
        const old = document.getElementById('crv-confirm-ok');
        const okBtn = old.cloneNode(false);   // drop listeners from the previous use
        okBtn.textContent = ok;
        okBtn.className = 'btn ' + okClass;
        old.replaceWith(okBtn);
        return new Promise(resolve => {
            let accepted = false;
            okBtn.addEventListener('click', () => { accepted = true; modal.hide(); }, { once: true });
            el.addEventListener('hidden.bs.modal', () => resolve(accepted), { once: true });
            modal.show();
        });
    };

    // Forms marked data-confirm-title ask before submitting. Body text is plain text.
    document.addEventListener('submit', e => {
        const form = e.target;
        if (!(form instanceof HTMLFormElement) || !form.dataset.confirmTitle || form.dataset.confirmed === '1') return;
        e.preventDefault();
        CRV.confirm({
            title: form.dataset.confirmTitle,
            html: '<p class="mb-0">' + esc(form.dataset.confirmBody || '') + '</p>',
            ok: form.dataset.confirmOk || 'Confirm',
            okClass: form.dataset.confirmClass || 'btn-warning',
        }).then(yes => {
            if (!yes) return;
            form.dataset.confirmed = '1';
            form.submit();
        });
    }, true);

    // ── Toast ────────────────────────────────────────────────────
    let toastTimer = 0;
    CRV.toast = function (text) {
        let t = document.querySelector('.crv-toast');
        if (!t) { t = document.createElement('div'); t.className = 'crv-toast'; t.setAttribute('role', 'status'); document.body.appendChild(t); }
        t.textContent = text;
        t.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(() => { t.hidden = true; }, 5000);
    };

    // ── Light / dark ─────────────────────────────────────────────
    // Saved per browser. Pages rebuilt for light mode apply it; the rest stay dark for now.
    (function () {
        const btn = document.getElementById('theme-toggle');
        if (!btn) return;
        const ready = document.body.dataset.lightReady === 'true';
        const pref = () => { try { return localStorage.getItem('crv-theme') || 'dark'; } catch (e) { return 'dark'; } };
        function show() {
            const light = pref() === 'light';
            btn.querySelector('i').className = 'bi ' + (light ? 'bi-moon' : 'bi-sun');
            btn.setAttribute('aria-label', light ? 'Switch to dark mode' : 'Switch to light mode');
            btn.setAttribute('aria-pressed', String(light));
        }
        btn.addEventListener('click', () => {
            const next = pref() === 'light' ? 'dark' : 'light';
            try { localStorage.setItem('crv-theme', next); } catch (e) { }
            show();
            if (ready) {
                document.documentElement.setAttribute('data-bs-theme', next);
                document.dispatchEvent(new CustomEvent('crv:theme', { detail: next }));
            } else if (next === 'light') {
                CRV.toast('Light mode is saved. Results, Sessions and Validation use it now; other pages switch as they are rebuilt.');
            }
        });
        show();
    })();

    // ── Engine bar ───────────────────────────────────────────────
    const bar = document.getElementById('crv-engine');
    if (!bar) return;
    const $ = id => document.getElementById(id);
    const realMoney = bar.dataset.realMoney === 'true';
    const account = bar.dataset.account;
    const broker = bar.dataset.broker;
    const setups = (bar.dataset.setups || '').split('\n').filter(Boolean);
    let running = bar.dataset.running === 'true';

    function money(v) {
        const n = Number(v) || 0;
        return (n >= 0 ? '+$' : '−$') + Math.abs(n).toLocaleString('en-US', { maximumFractionDigits: 0 });
    }

    function setState(text, cls) {
        const b = $('eng-state');
        b.className = cls;
        $('eng-state-text').textContent = text;
    }

    function setRunning(isRunning) {
        running = isRunning;
        bar.dataset.running = String(isRunning);
        const btn = $('eng-toggle');
        btn.className = 'crv-btn ' + (isRunning ? 'stop' : 'start');
        btn.setAttribute('aria-label', isRunning ? 'Stop the engine' : 'Start the engine');
        btn.querySelector('i').className = 'bi ' + (isRunning ? 'bi-stop-fill' : 'bi-play-fill');
        $('eng-toggle-text').textContent = isRunning ? 'Stop engine' : 'Start engine';
    }

    // status: string from the hub ("Live", "Session Ended", "Stopped", "Backfilling…", "Error: …"),
    // true/false from older callers, or "OFFLINE" when the socket drops.
    CRV.engine = {
        status(status) {
            if (status === true || status === 'Live') { setState('Running', 'up'); setRunning(true); }
            else if (status === false || status === 'Stopped') { setState('Stopped', 'mut'); setRunning(false); }
            else if (status === 'Session Ended') { setState('Session ended', 'warn'); setRunning(true); }
            else if (status === 'OFFLINE') { setState('No connection', 'down'); }
            else if (typeof status === 'string' && status.startsWith('Error')) { setState('Error', 'down'); }
            else if (typeof status === 'string' && status) { setState(status.replace(/…$/, ''), 'warn'); }
        },
        snapshot(s) {
            if (!s) return;
            const pnl = $('eng-pnl');
            pnl.textContent = money(s.todayPnl);
            pnl.className = s.todayPnl > 0 ? 'up' : s.todayPnl < 0 ? 'down' : '';
            if (s.ticker && $('nav-ticker')) $('nav-ticker').textContent = s.ticker;
            const meter = $('eng-loss');
            if (meter && s.dailyLossLimit > 0) {
                const pct = Math.max(0, Math.min(100, s.dailyLossUsed / s.dailyLossLimit * 100));
                meter.firstElementChild.style.width = pct.toFixed(1) + '%';
                meter.classList.toggle('hot', pct >= 75 || !!s.tradingHalted);
                $('eng-loss-txt').textContent = s.tradingHalted ? 'reached' : Math.round(pct) + '%';
            }
        },
        sync() {
            return fetch('/api/engine/status', { headers: { Accept: 'application/json' } })
                .then(r => r.ok ? r.json() : null)
                .then(d => {
                    if (!d) return d;
                    setRunning(!!d.running);
                    CRV.engine.status(d.running ? (d.snapshot?.sessionEnded ? 'Session Ended' : 'Live') : 'Stopped');
                    CRV.engine.snapshot(d.snapshot);
                    return d;
                })
                .catch(() => null);
        },
    };

    // ET clock
    const clock = $('eng-clock');
    if (clock) {
        const fmt = new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
        const tick = () => { clock.textContent = fmt.format(new Date()); };
        tick();
        setInterval(tick, 1000);
    }

    // Start / stop, always confirmed.
    const acctLine = '<p class="mb-2"><span class="crv-acct crv-confirm-acct ' + (realMoney ? '' : 'sim') + '">' + esc(account) + ' · ' + esc(broker) + '</span></p>';
    $('eng-toggle').addEventListener('click', async () => {
        const starting = !running;
        const html = starting
            ? acctLine +
              (realMoney ? '<p class="mb-2">Orders will go to your <strong>' + esc(broker) + '</strong> account. This is real money.</p>'
                         : '<p class="mb-2">Orders will go to a simulated account (' + esc(broker) + ').</p>') +
              (setups.length
                  ? '<p class="mb-0">These setups will arm:</p><ul>' + setups.map(s => '<li>' + esc(s) + '</li>').join('') + '</ul>'
                  : '<p class="mb-0 text-warning">No setup is switched on, so the engine won\'t place any trades.</p>')
            : acctLine +
              '<p class="mb-0">Open positions and their orders stay at the broker. The engine stops watching them, ' +
              'so it won\'t manage or close anything until you start it again.</p>';
        const yes = await CRV.confirm({
            title: starting ? 'Start the engine?' : 'Stop the engine?',
            html,
            ok: starting ? 'Start engine' : 'Stop engine',
            okClass: starting ? 'btn-success' : 'btn-danger',
        });
        if (!yes) return;
        const btn = $('eng-toggle');
        btn.disabled = true;
        try {
            const r = await fetch('/api/engine/' + (starting ? 'start' : 'stop'), { method: 'POST' });
            if (!r.ok && r.status !== 400) throw new Error('HTTP ' + r.status);
            CRV.toast(starting ? 'Engine starting…' : 'Engine stopped.');
        } catch (err) {
            CRV.toast('Couldn\'t ' + (starting ? 'start' : 'stop') + ' the engine (' + err.message + '). Check the app log.');
        } finally {
            btn.disabled = false;
            setTimeout(() => CRV.engine.sync(), 800);
        }
    });
})();
