// Setups A–D (the older per-session setups), shown on Sessions & risk only while no
// opening-range strategy exists. Moved unchanged from the Engine settings page.

// ── Setup row expand/collapse toggle ─────────────────────────
(function () {
    document.querySelectorAll('.setup-summary-row').forEach(function (row) {
        row.addEventListener('click', function () {
            var targetId = row.dataset.target;
            var detail = document.getElementById(targetId);
            if (!detail) return;
            var chevron = row.querySelector('.setup-chevron');
            if (detail.style.display === 'none') {
                detail.style.display = '';
                if (chevron) { chevron.classList.remove('bi-chevron-right'); chevron.classList.add('bi-chevron-down'); }
            } else {
                detail.style.display = 'none';
                if (chevron) { chevron.classList.remove('bi-chevron-down'); chevron.classList.add('bi-chevron-right'); }
            }
        });
    });
})();

// ── Per-setup custom instrument toggle + sync ──────────────────
(function () {
    // Checkbox: enable/disable the dropdown
    document.querySelectorAll('.setup-custom-instr-chk').forEach(function (chk) {
        chk.addEventListener('change', function () {
            var setup = chk.dataset.setup;
            var sess  = chk.dataset.session;
            var row   = chk.closest('.setup-detail-row') || chk.closest('td');
            var sel   = row.querySelector('.setup-custom-instr-sel[data-setup="' + setup + '"]');
            if (sel) sel.disabled = !chk.checked;
            if (chk.checked && sel) setupInstrOnChange(sel, setup, sess);
        });
    });

    // Dropdown: update hidden fields when instrument changes
    document.querySelectorAll('.setup-custom-instr-sel').forEach(function (sel) {
        sel.addEventListener('change', function () {
            setupInstrOnChange(sel, sel.dataset.setup, sel.dataset.session);
        });
    });

    function setupInstrOnChange(sel, setup, sess) {
        var base = sel.value;
        var info = window.CRV_findInstrument(base);
        if (!info) return;
        var ticker = window.CRV_activeContract(base);
        var row = sel.closest('.setup-detail-row') || sel.closest('td');
        var hTicker = row.querySelector('.setup-custom-ticker-hidden[data-setup="' + setup + '"]');
        var hPV     = row.querySelector('.setup-custom-pv-hidden[data-setup="' + setup + '"]');
        var hTS     = row.querySelector('.setup-custom-ts-hidden[data-setup="' + setup + '"]');
        var label   = row.querySelector('.setup-custom-instr-label[data-setup="' + setup + '"]');
        if (hTicker) hTicker.value = ticker;
        if (hPV)     hPV.value     = info.pointValue;
        if (hTS)     hTS.value     = info.tickSize;
        if (label)   label.textContent = ticker + ' ($' + info.pointValue + '/pt)';
    }

    // Init: sync dropdowns to saved values on page load
    document.querySelectorAll('.setup-custom-ticker-hidden').forEach(function (h) {
        var savedTicker = h.value;
        if (!savedTicker) return;
        var setup = h.dataset.setup;
        var row   = h.closest('.setup-detail-row') || h.closest('td');
        var sel   = row.querySelector('.setup-custom-instr-sel[data-setup="' + setup + '"]');
        if (!sel) return;
        var base  = window.CRV_parseBase(savedTicker);
        if (base) {
            sel.value = base;
            var info = window.CRV_findInstrument(base);
            var label = row.querySelector('.setup-custom-instr-label[data-setup="' + setup + '"]');
            if (info && label) {
                var front = window.CRV_activeContract(base);
                label.textContent = front + ' ($' + info.pointValue + '/pt)';
                // Update hidden ticker to front month (may have rolled)
                h.value = front;
            }
        }
    });
})();

function toggleSetupAutoTrail(checkbox, index, sl) {
    var section = document.getElementById('autotrail-setup-' + index + '-' + sl);
    if (section) section.classList.toggle('d-none', !checkbox.checked);
    var beField = document.querySelector('[data-session="' + index + '"][data-field="Setup' + sl + '.UseBe"]');
    if (beField) {
        if (checkbox.checked) { beField.checked = false; beField.disabled = true; }
        else { beField.disabled = false; }
    }
}
