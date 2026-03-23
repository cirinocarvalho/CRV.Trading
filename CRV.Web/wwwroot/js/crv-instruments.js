// ── CRV Futures Instrument Catalog ───────────────────────────
// Shared by Live Settings, Manual Trading, and Orders pages.
// Exposes: CRV_INSTRUMENTS, CRV_activeContract(), CRV_findInstrument(),
//          CRV_parseBase(), CRV_formatAll()

window.CRV_INSTRUMENTS = {
    mini: [
        { code: 'NQ',  label: 'NQ  — E-mini Nasdaq-100',  pointValue: 20,   tickSize: 0.25, expiry: 'quarterly'  },
        { code: 'ES',  label: 'ES  — E-mini S&P 500',     pointValue: 50,   tickSize: 0.25, expiry: 'quarterly'  },
        { code: 'YM',  label: 'YM  — E-mini Dow Jones',   pointValue: 5,    tickSize: 1.00, expiry: 'quarterly'  },
        { code: 'RTY', label: 'RTY — E-mini Russell 2000', pointValue: 50,   tickSize: 0.10, expiry: 'quarterly'  },
        { code: 'GC',  label: 'GC  — Gold',               pointValue: 100,  tickSize: 0.10, expiry: 'bimonthly'  },
        { code: 'CL',  label: 'CL  — Crude Oil',          pointValue: 1000, tickSize: 0.01, expiry: 'monthly'    },
        { code: 'BTC', label: 'BTC — Bitcoin',             pointValue: 5,    tickSize: 5.00, expiry: 'monthly'    },
    ],
    micro: [
        { code: 'MNQ', label: 'MNQ — Micro Nasdaq-100',   pointValue: 2,    tickSize: 0.25, expiry: 'quarterly'  },
        { code: 'MES', label: 'MES — Micro S&P 500',      pointValue: 5,    tickSize: 0.25, expiry: 'quarterly'  },
        { code: 'MYM', label: 'MYM — Micro Dow Jones',    pointValue: 0.50, tickSize: 1.00, expiry: 'quarterly'  },
        { code: 'M2K', label: 'M2K — Micro Russell 2000', pointValue: 5,    tickSize: 0.10, expiry: 'quarterly'  },
        { code: 'MGC', label: 'MGC — Micro Gold',          pointValue: 10,   tickSize: 0.10, expiry: 'bimonthly' },
        { code: 'MCL', label: 'MCL — Micro Crude Oil',     pointValue: 100,  tickSize: 0.01, expiry: 'monthly'   },
        { code: 'MBT', label: 'MBT — Micro Bitcoin',      pointValue: 0.10, tickSize: 5.00, expiry: 'monthly'   },
    ]
};

// All 12 month codes
const _ALL_CODES = { 1:'F', 2:'G', 3:'H', 4:'J', 5:'K', 6:'M', 7:'N', 8:'Q', 9:'U', 10:'V', 11:'X', 12:'Z' };

// ── Quarterly — NQ, ES, MNQ, MES (H/M/U/Z  ·  Mar/Jun/Sep/Dec) ──────────────
// Roll: after the 10th of an expiry month the next quarter becomes front.
const _QTR_MONTHS = [3, 6, 9, 12];
const _QTR_CODES  = { 3:'H', 6:'M', 9:'U', 12:'Z' };

function _quarterlySuffix() {
    const now   = new Date();
    const year  = now.getFullYear();
    const month = now.getMonth() + 1;
    const day   = now.getDate();
    const yr2   = String(year).slice(-2);

    if (_QTR_MONTHS.includes(month) && day <= 10) {
        return _QTR_CODES[month] + yr2;
    }
    const next = _QTR_MONTHS.find(m => m > month);
    if (next) return _QTR_CODES[next] + yr2;
    return 'H' + String(year + 1).slice(-2);
}

// ── Monthly — CL, MCL (all 12 month codes) ───────────────────────────────────
// CL expires ≈ 3 biz days before the 25th of the prior month (≈ 20th).
// Roll approximation: on/after 20th of month M, front contract is delivery M+2; else M+1.
function _monthlySuffix() {
    const now   = new Date();
    let   year  = now.getFullYear();
    const month = now.getMonth() + 1;
    const day   = now.getDate();

    let cm = month + (day >= 20 ? 2 : 1);
    if (cm > 12) { cm -= 12; year++; }

    return _ALL_CODES[cm] + String(year).slice(-2);
}

// ── Bi-monthly — GC, MGC (G/J/M/Q/V/Z  ·  Feb/Apr/Jun/Aug/Oct/Dec) ──────────
// GC first notice ≈ last biz day of prior month; roll approximation: 25th of prior month.
const _GC_MONTHS = [2, 4, 6, 8, 10, 12];
const _GC_CODES  = { 2:'G', 4:'J', 6:'M', 8:'Q', 10:'V', 12:'Z' };

function _biMonthlySuffix() {
    const now  = new Date();
    const year = now.getFullYear();

    for (const m of _GC_MONTHS) {
        // rollDate = 25th of the prior month (0-based: m-2)
        if (now < new Date(year, m - 2, 25)) {
            return _GC_CODES[m] + String(year).slice(-2);
        }
    }
    // Past Dec roll → Feb next year
    return 'G' + String(year + 1).slice(-2);
}

// ── Public API ────────────────────────────────────────────────────────────────

// Returns the active contract suffix for baseCode (e.g. "H26", "J26", "Q26")
window.CRV_contractSuffix = function (baseCode) {
    const instr = window.CRV_findInstrument(baseCode);
    if (!instr) return _quarterlySuffix();   // safe fallback
    switch (instr.expiry) {
        case 'monthly':    return _monthlySuffix();
        case 'bimonthly':  return _biMonthlySuffix();
        default:           return _quarterlySuffix();
    }
};

// Returns the full active ticker, e.g. "NQH26", "CLJ26", "GCM26"
window.CRV_activeContract = function (baseCode) {
    return baseCode + window.CRV_contractSuffix(baseCode);
};

// Returns all broker-specific formats for display purposes
window.CRV_formatAll = function (baseCode) {
    const contract = window.CRV_activeContract(baseCode);
    return {
        canonical:    contract,        // e.g. NQH26  (stored in DB / TradeStation)
        schwab:       '/' + contract,  // e.g. /NQH26
        tradestation: contract
    };
};

// Find instrument metadata by base code
window.CRV_findInstrument = function (code) {
    const all = [...window.CRV_INSTRUMENTS.mini, ...window.CRV_INSTRUMENTS.micro];
    return all.find(i => i.code === code) ?? null;
};

// ── Per-Broker Commission Rates (per side) ──────────────────────────────────
window.CRV_COMMISSIONS = {
    Schwab:       { micro: 2.25, mini: 2.25 },
    TradeStation: { micro: 0.75, mini: 2.00 },
    Tradovate:    { micro: 0.59, mini: 1.59 },
    Mock:         { micro: 0.00, mini: 0.00 },
};

// Returns the commission per side for a given broker and ticker base code.
// Falls back to 2.25 (Schwab default) if broker is unknown.
window.CRV_getCommission = function (broker, tickerBase) {
    var rates = window.CRV_COMMISSIONS[broker];
    if (!rates) return 2.25;
    var isMicro = tickerBase && tickerBase.startsWith('M');
    return isMicro ? rates.micro : rates.mini;
};

// Parse base code from a full ticker like "CLJ26" or "/GCM26" → "CL" / "GC"
window.CRV_parseBase = function (ticker) {
    if (!ticker) return null;
    const t = ticker.replace(/^\//, '');   // strip leading /
    // Check longer codes first to avoid partial match (MNQ before NQ, M2K before RTY, etc.)
    const codes = ['MNQ', 'MES', 'MYM', 'M2K', 'MGC', 'MCL', 'MBT', 'NQ', 'ES', 'YM', 'RTY', 'GC', 'CL', 'BTC'];
    for (const code of codes) {
        if (t.startsWith(code)) return code;
    }
    return null;
};
