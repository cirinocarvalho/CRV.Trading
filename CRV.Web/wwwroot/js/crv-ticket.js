// CRV Trading — order ticket drawer (_OrderTicket.cshtml).
// The preview mirrors ManualOrderBuilder's math so you see the bracket before sending;
// the server re-validates and builds the real order.
(function () {
    'use strict';
    const el = document.getElementById('ticket');
    if (!el || !window.bootstrap || !window.CRV || !window.CRV_INSTRUMENTS) return;
    const $ = id => document.getElementById(id);
    const esc = CRV.esc;
    const canvas = bootstrap.Offcanvas.getOrCreateInstance(el);
    const state = { side: 'Long', type: 'Market', mode: 'Points' };
    const engine = () => document.getElementById('crv-engine')?.dataset || {};

    // ── Instruments: basket first, then the catalog ──
    const all = [...CRV_INSTRUMENTS.micro, ...CRV_INSTRUMENTS.mini];
    const byCode = Object.fromEntries(all.map(i => [i.code, i]));
    const basket = (el.dataset.basket || '').split(',').filter(c => byCode[c]);
    const sel = $('tk-instr');
    const group = (label, list) => `<optgroup label="${esc(label)}">` +
        list.map(i => `<option value="${i.code}">${esc(CRV_activeContract(i.code))} · ${esc(i.label.split('—')[1]?.trim() || i.code)} · $${i.pointValue}/pt</option>`).join('') + '</optgroup>';
    sel.innerHTML = (basket.length ? group('In your basket', basket.map(c => byCode[c])) : '') +
        group('Micro', CRV_INSTRUMENTS.micro.filter(i => !basket.includes(i.code))) +
        group('E-mini', CRV_INSTRUMENTS.mini.filter(i => !basket.includes(i.code)));
    if (byCode[el.dataset.default]) sel.value = el.dataset.default;
    $('tk-cts').value = el.dataset.contracts || 1;

    const instr = () => byCode[sel.value];
    const ticker = () => CRV_activeContract(sel.value);
    const n = id => { const v = parseFloat($(id).value); return isFinite(v) ? v : 0; };
    const fmt = v => Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const usd = v => '$' + Math.round(Math.abs(v)).toLocaleString('en-US');

    async function loadPrice() {
        $('tk-sub').textContent = ticker();
        try {
            const r = await fetch('/api/engine/price/' + encodeURIComponent(ticker()));
            const d = r.ok ? await r.json() : null;
            if (d && d.price > 0) {
                $('tk-entry').value = d.price.toFixed(2);
                $('tk-sub').textContent = `${ticker()} · last ${fmt(d.price)}`;
            } else {
                $('tk-sub').textContent = `${ticker()} · no live price, enter one`;
            }
        } catch (e) { /* leave the field for the user */ }
        update();
    }

    // ── Segmented controls ──
    el.querySelectorAll('[data-side],[data-type],[data-mode]').forEach(b => b.addEventListener('click', () => {
        const key = b.dataset.side ? 'side' : b.dataset.type ? 'type' : 'mode';
        state[key] = b.dataset[key === 'side' ? 'side' : key === 'type' ? 'type' : 'mode'];
        b.parentElement.querySelectorAll('button').forEach(x => {
            const on = x === b;
            x.classList.toggle('active', on);
            x.setAttribute('aria-checked', String(on));
        });
        update();
    }));
    el.querySelectorAll('[data-step]').forEach(b => b.addEventListener('click', () => {
        $('tk-cts').value = Math.max(1, Math.min(100, (parseInt($('tk-cts').value, 10) || 1) + parseInt(b.dataset.step, 10)));
        update();
    }));
    sel.addEventListener('change', loadPrice);
    el.addEventListener('input', update);
    el.addEventListener('change', update);

    // ── Build the request and the preview (ManualOrderBuilder's math) ──
    function request() {
        const multi = $('tk-multi').checked;
        const mode = multi ? 'Price' : state.mode;
        const o = {
            ticker: ticker(), direction: state.side, orderType: state.type, inputMode: mode,
            entryPrice: n('tk-entry'), contracts: parseInt($('tk-cts').value, 10) || 0, pointValue: instr().pointValue,
            useBe: $('tk-be').checked, usePartial: !multi && $('tk-partial').checked, partialContracts: parseInt($('tk-partial-cts').value, 10) || 0,
            useMultiBracket: multi, useAutoTrail: $('tk-trail').checked,
            autoTrailStopLoss: n('tk-trail-sl'), autoTrailTrigger: n('tk-trail-trig'), autoTrailFreq: n('tk-trail-freq'),
            brackets: [...el.querySelectorAll('.c-ticket-bracket')].map(r => ({
                target: parseFloat(r.querySelector('[data-f="target"]').value) || 0,
                qty: parseInt(r.querySelector('[data-f="qty"]').value, 10) || 0,
                moveBe: r.querySelector('[data-f="be"]').checked,
            })),
        };
        const stop = n('tk-stop'), target = n('tk-target'), partial = n('tk-partial-val');
        if (mode === 'Points')  { o.stopPoints = stop; o.targetPoints = target; o.partialPoints = partial; }
        if (mode === 'Dollars') { o.stopDollars = stop; o.targetDollars = target; o.partialDollars = partial; }
        if (mode === 'Price')   { o.stopPrice = stop; o.targetPrice = target; o.partialPrice = partial; }
        return o;
    }

    function preview(o) {
        const long = o.direction === 'Long', pv = o.pointValue, e = o.entryPrice, c = o.contracts;
        if (!(e > 0) || !(c > 0)) return null;
        if (o.useMultiBracket) {
            const rows = o.brackets.filter(b => b.target > 0 && b.qty > 0);
            if (!(o.stopPrice > 0) || !rows.length) return null;
            const risk = Math.abs(e - o.stopPrice) * pv * c;
            const reward = rows.reduce((s, b) => s + Math.abs(b.target - e) * b.qty * pv, 0);
            return { stop: o.stopPrice, targets: rows.map(b => b.target), risk, reward };
        }
        let sp = o.stopPoints, tp = o.targetPoints, pp = o.partialPoints || 0;
        const remain = c - (o.usePartial ? o.partialContracts : 0);
        if (o.inputMode === 'Dollars') {
            sp = o.stopDollars / (pv * c);
            tp = o.targetDollars / (pv * (o.usePartial ? Math.max(1, remain) : c));
            pp = o.usePartial && o.partialContracts > 0 ? o.partialDollars / (pv * o.partialContracts) : 0;
        } else if (o.inputMode === 'Price') {
            sp = long ? e - o.stopPrice : o.stopPrice - e;
            tp = long ? o.targetPrice - e : e - o.targetPrice;
            pp = o.usePartial ? (long ? o.partialPrice - e : e - o.partialPrice) : 0;
        }
        if (!(sp > 0) || !(tp > 0)) return null;
        const dir = long ? 1 : -1;
        const targets = o.usePartial && pp > 0 ? [e + dir * pp, e + dir * tp] : [e + dir * tp];
        const reward = o.usePartial && pp > 0 ? pp * pv * o.partialContracts + tp * pv * remain : tp * pv * c;
        return { stop: e - dir * sp, targets, risk: sp * pv * c, reward };
    }

    function update() {
        const multi = $('tk-multi').checked;
        const mode = multi ? 'Price' : state.mode;
        const unit = mode === 'Points' ? 'points' : mode === 'Dollars' ? 'dollars' : 'price';
        $('tk-stop-label').textContent = `Stop (${unit})`;
        $('tk-target-label').textContent = `Target (${unit})`;
        $('tk-partial-label').textContent = `First target (${unit})`;
        $('tk-entry-label').textContent = state.type === 'Limit' ? 'Limit price' : 'Reference price';
        $('tk-partial-fields').hidden = !$('tk-partial').checked || multi;
        $('tk-partial-row').hidden = multi;
        $('tk-target-wrap').hidden = multi;
        $('tk-trail-fields').hidden = !$('tk-trail').checked;
        $('tk-multi-fields').hidden = !multi;
        el.querySelectorAll('[data-mode]').forEach(b => { b.disabled = multi; });

        const eng = engine();
        $('tk-engine-note').hidden = eng.running !== 'false';
        const o = request(), p = preview(o);
        const place = $('tk-place');
        place.classList.toggle('sim', eng.realMoney === 'false');
        if (!p) {
            $('tk-preview').innerHTML = '';
            $('tk-summary').textContent = 'Fill in a price, a stop and a target.';
            place.disabled = true;
            place.querySelector('span').textContent = 'Hold to place';
            return;
        }
        const rr = p.risk > 0 ? (p.reward / p.risk).toFixed(2) : '—';
        $('tk-preview').innerHTML =
            `<span>Stop <b>${fmt(p.stop)}</b></span><span>Target${p.targets.length > 1 ? 's' : ''} <b>${p.targets.map(fmt).join(' / ')}</b></span>` +
            `<span class="c-down">Risk ${usd(p.risk)}</span><span class="c-up">Reward ${usd(p.reward)}</span><span>${rr}R</span>`;
        const verb = o.direction === 'Long' ? 'Buy' : 'Sell';
        $('tk-summary').textContent = `${verb} ${o.contracts} ${o.ticker} ${o.orderType === 'Limit' ? 'limit ' + fmt(o.entryPrice) : 'at market'} · stop ${fmt(p.stop)} · target ${p.targets.map(fmt).join(' / ')}`;
        place.disabled = false;
        place.querySelector('span').textContent = `Hold to ${verb.toLowerCase()} · ${eng.account || ''}`;
    }

    function showErrors(list) {
        const ul = $('tk-errors');
        ul.innerHTML = list.map(e => `<li>${esc(e)}</li>`).join('');
        ul.hidden = list.length === 0;
        if (list.length) ul.scrollIntoView({ block: 'nearest' });
    }

    CRV.hold($('tk-place'), async () => {
        const place = $('tk-place');
        place.disabled = true;
        place.querySelector('span').textContent = 'Placing…';
        showErrors([]);
        try {
            const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
            const r = await fetch('/api/orders/ticket', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token },
                body: JSON.stringify(request()),
            });
            const d = await r.json().catch(() => ({}));
            if (r.ok) {
                CRV.toast(d.message || 'Order placed.');
                canvas.hide();
                document.dispatchEvent(new CustomEvent('crv:book-changed'));
            } else {
                // Builder errors arrive as { errors: [...] }; model-binding errors as ProblemDetails.
                const list = Array.isArray(d.errors) ? d.errors
                    : d.errors ? Object.values(d.errors).flat() : [d.title || ('HTTP ' + r.status)];
                showErrors(list);
            }
        } catch (err) {
            showErrors(['No response from the app (' + err.message + '). Check Positions & orders before trying again.']);
        } finally {
            update();
        }
    });

    // ── Open ──
    document.querySelectorAll('[data-open-ticket]').forEach(b => b.addEventListener('click', e => { e.preventDefault(); canvas.show(); }));
    el.addEventListener('show.bs.offcanvas', () => { showErrors([]); loadPrice(); });
    if (new URLSearchParams(location.search).get('ticket') === '1') canvas.show();
    update();
})();
