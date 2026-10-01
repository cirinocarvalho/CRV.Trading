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

    // ── Engine bar height, as --crv-engine-h, for anything else that sticks to the top ─
    // The bar is sticky at top: 0 and wraps differently by width, so its height is measured.
    (function () {
        const bar = document.querySelector('.crv-engine');
        if (!bar) return;
        const set = () => document.documentElement.style.setProperty('--crv-engine-h', bar.offsetHeight + 'px');
        set();
        if (window.ResizeObserver) new ResizeObserver(set).observe(bar);
    })();

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

    // ── POST with the antiforgery token (layout renders it once) ─
    CRV.post = async function (url, body) {
        const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
        const r = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
            body: body === undefined ? undefined : JSON.stringify(body),
        });
        let data = null;
        try { data = await r.json(); } catch (e) { /* empty body */ }
        if (!r.ok) throw new Error((data && (data.error || data.title)) || ('HTTP ' + r.status));
        return data;
    };

    // ── Press-and-hold: a stray tap can't trigger an action that moves money ─
    // CRV.hold(button, onDone) — onDone runs once the button has been held for `ms`.
    CRV.hold = function (btn, onDone, ms) {
        const need = ms || (matchMedia('(prefers-reduced-motion: reduce)').matches ? 700 : 1000);
        const fill = btn.querySelector('i');
        let t0 = 0, raf = 0;
        const reset = () => { cancelAnimationFrame(raf); if (fill) fill.style.width = '0'; };
        const tick = now => {
            const k = Math.min(1, (now - t0) / need);
            if (fill) fill.style.width = (k * 100) + '%';
            if (k >= 1) { reset(); onDone(); return; }
            raf = requestAnimationFrame(tick);
        };
        const start = e => { if (btn.disabled) return; e.preventDefault(); t0 = performance.now(); raf = requestAnimationFrame(tick); };
        btn.addEventListener('pointerdown', start);
        ['pointerup', 'pointerleave', 'pointercancel'].forEach(ev => btn.addEventListener(ev, reset));
        btn.addEventListener('keydown', e => { if ((e.key === ' ' || e.key === 'Enter') && !e.repeat) start(e); });
        btn.addEventListener('keyup', e => { if (e.key === ' ' || e.key === 'Enter') reset(); });
        btn.addEventListener('click', e => e.preventDefault());
    };

    // ── Flatten all ──────────────────────────────────────────────
    (function () {
        const open = document.getElementById('flatten-open');
        const sheetEl = document.getElementById('flatten-sheet');
        if (!open || !sheetEl || !window.bootstrap) return;
        const body = document.getElementById('flatten-body');
        const hold = document.getElementById('flatten-hold');
        const label = hold.querySelector('span');
        const title = document.getElementById('flatten-title');
        const sheet = new bootstrap.Modal(sheetEl);
        let busy = false;

        const li = (a, b) => `<li><span>${esc(a)}</span><span>${esc(b)}</span></li>`;

        function render(p) {
            const plan = p.plan;
            const acct = `<span class="crv-acct ${p.realMoney ? '' : 'sim'}">${esc(p.account)} · ${esc(p.broker)}</span>`;
            let html = `<p class="mb-2">${acct}</p>` +
                `<p class="mb-1">${p.realMoney ? 'This uses real money. ' : ''}Everything below happens at market, then the engine stops so nothing re-arms.</p>`;
            if (p.problem) html += `<div class="c-note bad my-2"><i class="bi bi-exclamation-octagon"></i><span>${esc(p.problem)}</span></div>`;
            if (plan.groups.length) html += `<div class="c-plan-h">Close through the engine</div><ul class="c-plan">` +
                plan.groups.map(g => li(`${g.setup} · ${g.ticker}`, g.status === 'Pending' ? 'cancel unfilled entry' : `${g.direction.toLowerCase()} ${g.contracts} at market`)).join('') + '</ul>';
            if (plan.positions.length) html += `<div class="c-plan-h">Close at the broker</div><ul class="c-plan">` +
                plan.positions.map(x => li(x.symbol, `${x.isLong ? 'long' : 'short'} ${x.quantity}: cancel orders, close at market`)).join('') + '</ul>';
            if (plan.orderSymbols.length) html += `<div class="c-plan-h">Cancel working orders</div><ul class="c-plan">` +
                plan.orderSymbols.map(sym => li(sym, 'all working orders')).join('') + '</ul>';
            if (plan.leftAlone.length) html += `<div class="c-plan-h">Not touched</div><ul class="c-plan">` +
                plan.leftAlone.map(x => li(x, 'close in Options')).join('') + '</ul>';
            if (p.engineRunning) html += `<div class="c-plan-h">Then</div><ul class="c-plan">${li('Stop the engine', 'until you start it again')}</ul>`;
            if (plan.groups.length + plan.positions.length + plan.orderSymbols.length === 0 && !p.engineRunning)
                html += `<p class="c-mut mt-2 mb-0">Nothing is open, and the engine isn't running. There's nothing to flatten.</p>`;
            body.innerHTML = html;

            const nothing = plan.groups.length + plan.positions.length + plan.orderSymbols.length === 0 && !p.engineRunning;
            hold.disabled = nothing;
            hold.classList.toggle('sim', !p.realMoney);
            label.textContent = nothing ? 'Nothing to flatten' : `Hold to flatten · ${p.account}`;
        }

        open.addEventListener('click', async () => {
            if (busy) return;
            title.textContent = 'Flatten everything?';
            body.innerHTML = '<p class="c-mut mb-0"><span class="spinner-border spinner-border-sm me-2" aria-hidden="true"></span>Checking what is open…</p>';
            hold.disabled = true; hold.hidden = false; label.textContent = 'Hold to flatten';
            sheet.show();
            try {
                const r = await fetch('/api/orders/flatten-all', { headers: { Accept: 'application/json' } });
                if (!r.ok) throw new Error('HTTP ' + r.status);
                render(await r.json());
            } catch (err) {
                body.innerHTML = `<div class="c-note bad"><i class="bi bi-exclamation-octagon"></i><span>Couldn't check what is open (${esc(err.message)}). Nothing was sent. Try again, or close positions at the broker.</span></div>`;
            }
        });

        CRV.hold(hold, async () => {
            busy = true;
            hold.disabled = true;
            label.textContent = 'Flattening…';
            try {
                const res = await CRV.post('/api/orders/flatten-all');
                title.textContent = res.ok ? 'Flattened' : 'Flatten all finished with problems';
                body.innerHTML =
                    (res.done.length ? `<div class="c-plan-h">Done</div><ul class="c-plan">${res.done.map(d => `<li><span>${esc(d)}</span></li>`).join('')}</ul>` : '') +
                    (res.failed.length ? `<div class="c-plan-h">Needs your attention</div><ul class="c-plan">${res.failed.map(d => `<li><span class="c-down">${esc(d)}</span></li>`).join('')}</ul>` : '') +
                    (res.leftAlone.length ? `<div class="c-plan-h">Not touched</div><ul class="c-plan">${res.leftAlone.map(d => `<li><span>${esc(d)}</span></li>`).join('')}</ul>` : '');
                hold.hidden = true;
                CRV.toast(res.ok ? 'Flatten all done. The engine is stopped.' : 'Flatten all finished with problems. Check the list.');
                document.dispatchEvent(new CustomEvent('crv:book-changed'));
            } catch (err) {
                title.textContent = 'Flatten all failed';
                body.innerHTML = `<div class="c-note bad"><i class="bi bi-exclamation-octagon"></i><span>${esc(err.message)}. Some orders may have been sent. Check Positions & orders and the broker now.</span></div>`;
                hold.hidden = true;
            } finally {
                busy = false;
                CRV.engine?.sync?.();
            }
        });
    })();

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
