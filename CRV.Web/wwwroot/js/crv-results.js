// CRV Trading — results view (_ResultsView.cshtml): filters, equity chart, CSV export.
// The server renders the unfiltered numbers with BacktestResultCalculator. When a filter is set,
// the same figures are recomputed here with the same formulas, and cleared filters restore the
// server-rendered markup.
(function () {
    'use strict';
    const esc = s => (window.CRV && CRV.esc) ? CRV.esc(s) : String(s);

    const money   = v => (v >= 0 ? '+$' : '−$') + Math.round(Math.abs(v)).toLocaleString('en-US');
    const dollars = v => '$' + Math.round(Math.abs(v)).toLocaleString('en-US');
    const tone    = v => v > 0 ? 'c-up' : v < 0 ? 'c-down' : '';
    const fmtR    = v => (Math.abs(v) < 0.005 ? '0.00' : (v > 0 ? '+' : '−') + Math.abs(v).toFixed(2)) + 'R';
    const pfText  = m => m.pf === 0 && m.net > 0 ? '∞' : m.pf.toFixed(2);

    // Mirrors BacktestResultCalculator.Calc.
    function metrics(trades) {
        const n = trades.length;
        if (!n) return { n: 0, wins: 0, losses: 0, winRate: 0, net: 0, comm: 0, pf: 0, avgWin: 0, avgLoss: 0, avgR: 0, maxDd: 0, cw: 0, cl: 0, longs: 0, shorts: 0, tgt: 0, stp: 0, eod: 0, exp: 0 };
        const wins = trades.filter(t => t.net > 0), losses = trades.filter(t => !(t.net > 0));
        const gW = wins.reduce((s, t) => s + t.net, 0), gL = Math.abs(losses.reduce((s, t) => s + t.net, 0));
        const byExit = trades.slice().sort((a, b) => a.exitedAt < b.exitedAt ? -1 : a.exitedAt > b.exitedAt ? 1 : 0);
        let peak = 0, eq = 0, maxDd = 0, cW = 0, cL = 0, mW = 0, mL = 0;
        byExit.forEach(t => {
            eq += t.net; if (eq > peak) peak = eq; maxDd = Math.max(maxDd, peak - eq);
            if (t.net > 0) { cW++; cL = 0; mW = Math.max(mW, cW); } else { cL++; cW = 0; mL = Math.max(mL, cL); }
        });
        const winRate = wins.length / n * 100;
        const avgWin = wins.length ? gW / wins.length : 0;
        const avgLoss = losses.length ? losses.reduce((s, t) => s + t.net, 0) / losses.length : 0;
        return {
            n, wins: wins.length, losses: losses.length, winRate,
            net: trades.reduce((s, t) => s + t.net, 0),
            comm: trades.reduce((s, t) => s + t.comm, 0),
            pf: gL > 0 ? Math.round(gW / gL * 100) / 100 : 0,
            avgWin, avgLoss,
            avgR: trades.reduce((s, t) => s + t.r, 0) / n,
            maxDd, cw: mW, cl: mL,
            longs: trades.filter(t => t.dir === 'Long').length,
            shorts: trades.filter(t => t.dir === 'Short').length,
            tgt: trades.filter(t => t.reason === 'Target').length,
            stp: trades.filter(t => t.reason === 'Stop').length,
            eod: trades.filter(t => t.reason === 'SessionEnd').length,
            exp: (winRate / 100) * avgWin + ((100 - winRate) / 100) * avgLoss,
        };
    }

    function tilesHtml(m) {
        const pfCls = m.pf >= 1.5 ? 'c-up' : (m.pf < 1 && m.n > 0 && !(m.pf === 0 && m.net > 0)) ? 'c-down' : '';
        return `<div class="c-tile"><small>Net P&amp;L</small><b class="${tone(m.net)}">${money(m.net)}</b></div>` +
               `<div class="c-tile"><small>Win rate</small><b>${m.winRate.toFixed(1)}%</b></div>` +
               `<div class="c-tile"><small>Profit factor</small><b class="${pfCls}">${pfText(m)}</b></div>` +
               `<div class="c-tile"><small>Max drawdown</small><b class="${m.maxDd > 0 ? 'c-down' : ''}">${m.maxDd > 0 ? '−' : ''}${dollars(m.maxDd)}</b></div>`;
    }

    function detailsHtml(m) {
        const kv = (k, v, cls) => `<div class="c-kv"><span>${k}</span><span class="${cls || ''}">${v}</span></div>`;
        return kv('Trades', `${m.n} (${m.wins} W / ${m.losses} L)`) +
               kv('Avg win / loss', `<span class="c-up">${dollars(m.avgWin)}</span> / <span class="c-down">−${dollars(m.avgLoss)}</span>`) +
               kv('Expectancy', money(m.exp) + ' / trade', tone(m.exp)) +
               kv('Avg R', fmtR(m.avgR), tone(m.avgR)) +
               kv('Most wins / losses in a row', `${m.cw} / ${m.cl}`) +
               kv('Long / short', `${m.longs} / ${m.shorts}`) +
               kv('Target / stop / end of day', `${m.tgt} / ${m.stp} / ${m.eod}`) +
               kv('Commission', '−' + dollars(m.comm), 'c-down');
    }

    function breakHtml(trades) {
        const groups = {};
        trades.forEach(t => (groups[t.setup] = groups[t.setup] || []).push(t));
        return Object.keys(groups).map(k => [k, metrics(groups[k])]).sort((a, b) => b[1].net - a[1].net).map(([k, m]) =>
            `<details><summary><span class="s ${m.net > 0 ? 'up' : m.net < 0 ? 'down' : ''}"></span>` +
            `<span><b>${esc(k)}</b><small>${m.n} trades · ${m.winRate.toFixed(1)}% · PF ${pfText(m)}</small></span>` +
            `<span class="rr ${tone(m.net)}">${money(m.net)}<small>${fmtR(m.avgR)} avg</small></span>` +
            `<i class="bi bi-chevron-right chev"></i></summary><div class="c-panel-b">${detailsHtml(m)}</div></details>`).join('');
    }

    function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }

    function initResults(root) {
        const dataEl = root.querySelector('.c-results-data');
        if (!dataEl) return;
        const data = JSON.parse(dataEl.textContent);
        const all = data.trades;
        if (!all.length) return;

        const parts = name => root.querySelector(`[data-part="${name}"]`);
        const original = { tiles: parts('tiles').innerHTML, more: parts('more').innerHTML, break: parts('break').innerHTML };
        const selects = [...root.querySelectorAll('[data-filter]')];
        const etFmt = new Intl.DateTimeFormat('en-US', data.singleDay
            ? { timeZone: 'America/New_York', hour: '2-digit', minute: '2-digit', hourCycle: 'h23' }
            : { timeZone: 'America/New_York', month: 'numeric', day: 'numeric' });

        // ── Chart ────────────────────────────────────────────
        let chart = null, current = { labels: data.curve.map(p => p.t), values: data.curve.map(p => p.v) };
        function drawChart() {
            const canvas = root.querySelector('canvas');
            if (!canvas || !window.Chart) return;
            if (chart) chart.destroy();
            const last = current.values.length ? current.values[current.values.length - 1] : 0;
            const line = last >= 0 ? cssVar('--c-up') : cssVar('--c-down');
            const fill = last >= 0 ? cssVar('--c-up-soft') : cssVar('--c-down-soft');
            const grid = cssVar('--c-line'), muted = cssVar('--c-muted');
            chart = new Chart(canvas.getContext('2d'), {
                type: 'line',
                data: {
                    labels: current.labels,
                    datasets: [
                        { data: current.values, borderColor: line, backgroundColor: fill, borderWidth: 2, fill: true, tension: 0.15,
                          pointRadius: current.values.length <= 80 ? 2.5 : 0, pointHoverRadius: 5 },
                        { data: current.values.map(() => 0), borderColor: grid, borderDash: [4, 4], borderWidth: 1, pointRadius: 0, fill: false },
                    ],
                },
                options: {
                    responsive: true, maintainAspectRatio: false, animation: false,
                    interaction: { mode: 'index', intersect: false },
                    plugins: { legend: { display: false },
                               tooltip: { filter: i => i.datasetIndex === 0, callbacks: { label: c => ' ' + money(c.parsed.y) } } },
                    scales: {
                        x: { ticks: { maxTicksLimit: 10, color: muted, font: { family: 'IBM Plex Mono', size: 11 } }, grid: { color: grid } },
                        y: { ticks: { color: muted, font: { family: 'IBM Plex Mono', size: 11 }, callback: v => '$' + Number(v).toLocaleString('en-US') }, grid: { color: grid } },
                    },
                },
            });
        }
        drawChart();
        document.addEventListener('crv:theme', drawChart);

        // ── Filters ──────────────────────────────────────────
        function filtered() {
            const f = {};
            selects.forEach(s => { if (s.value) f[s.dataset.filter] = s.value; });
            return { f, list: all.map((t, i) => [t, i]).filter(([t]) => Object.keys(f).every(k => t[k] === f[k])) };
        }

        function apply() {
            const { f, list } = filtered();
            const active = Object.keys(f).length > 0;
            const keep = new Set(list.map(([, i]) => i));
            root.querySelectorAll('[data-i]').forEach(el => { el.hidden = !keep.has(+el.dataset.i); });
            let n = 0;
            root.querySelectorAll('tr[data-i]').forEach(tr => { if (!tr.hidden) tr.querySelector('[data-part="n"]').textContent = ++n; });

            const trades = list.map(([t]) => t);
            parts('count').textContent = active ? `${trades.length} of ${all.length} trades` : `${all.length} trades`;
            parts('eq-count').textContent = `${trades.length} trades`;
            if (active) {
                const m = metrics(trades);
                parts('tiles').innerHTML = tilesHtml(m);
                parts('more').innerHTML = detailsHtml(m);
                parts('break').innerHTML = breakHtml(trades);
                const byExit = trades.slice().sort((a, b) => a.exitedAt < b.exitedAt ? -1 : 1);
                let eq = 0;
                current = { labels: byExit.map(t => etFmt.format(new Date(t.exitedAt))), values: byExit.map(t => (eq += t.net)) };
            } else {
                parts('tiles').innerHTML = original.tiles;
                parts('more').innerHTML = original.more;
                parts('break').innerHTML = original.break;
                current = { labels: data.curve.map(p => p.t), values: data.curve.map(p => p.v) };
            }
            drawChart();

            const url = new URL(location.href);
            selects.forEach(s => s.value ? url.searchParams.set(s.dataset.filter, s.value) : url.searchParams.delete(s.dataset.filter));
            history.replaceState(null, '', url);
        }

        const params = new URLSearchParams(location.search);
        let preset = false;
        selects.forEach(s => {
            const v = params.get(s.dataset.filter);
            if (v && [...s.options].some(o => o.value === v)) { s.value = v; preset = true; }
            s.addEventListener('change', apply);
        });
        if (preset) apply();

        // ── CSV ──────────────────────────────────────────────
        root.querySelector('[data-action="csv"]')?.addEventListener('click', () => {
            const head = ['Entered (UTC)', 'Exited (UTC)', 'Session', 'Setup', 'Instrument', 'Side', 'Entry', 'Exit', 'Stop', 'Target',
                          'Contracts', 'Partial', 'Exit reason', 'Source', 'Gross', 'Commission', 'Net', 'R'];
            const q = v => '"' + String(v ?? '').replace(/"/g, '""') + '"';
            const rows = filtered().list.map(([t]) => [t.enteredAt, t.exitedAt, t.session, t.setup, t.ticker, t.dir, t.entry, t.exit, t.stop,
                t.target, t.contracts, t.partial ? 'Yes' : 'No', t.reason, t.source, t.gross, t.comm, t.net, t.r].map(q).join(','));
            const blob = new Blob([[head.map(q).join(','), ...rows].join('\n')], { type: 'text/csv;charset=utf-8;' });
            const a = document.createElement('a');
            a.href = URL.createObjectURL(blob);
            a.download = (root.dataset.csv || 'trades') + '.csv';
            a.click();
            setTimeout(() => URL.revokeObjectURL(a.href), 1000);
        });
    }

    document.querySelectorAll('.c-results').forEach(initResults);
})();
