// Σύντομο "ding" όταν στέλνεται επιτυχώς μια παραγγελία στην κουζίνα/μπαρ.
// Χρησιμοποιεί το Web Audio API, δεν χρειάζεται αρχείο ήχου.
window.restaurantOrders = window.restaurantOrders || {};
window.restaurantOrders.isWideTableBoard = function () {
    return window.matchMedia('(min-width: 960px) and (min-height: 600px)').matches;
};
window.restaurantOrders.observeProductGrid = function (panel, dotNetRef) {
    if (!panel || !dotNetRef) return;
    if (panel._productGridCleanup) panel._productGridCleanup();

    let frame = 0;
    let lastCapacity = 0;
    let lastColumns = 0;
    let lastRows = 0;

    const measure = function () {
        frame = 0;
        const grid = panel.querySelector('.item-grid');
        if (!grid) return;

        const width = grid.clientWidth;
        const height = grid.clientHeight;
        if (width < 20 || height < 20) return;

        const style = window.getComputedStyle(grid);
        const columnGap = Number.parseFloat(style.columnGap) || 5;
        const rowGap = Number.parseFloat(style.rowGap) || 5;
        const compact = window.innerWidth <= 820;
        const minimumTileWidth = compact ? (window.innerWidth <= 390 ? 86 : 94) : 112;
        const minimumTileHeight = compact ? 44 : 50;
        const columns = Math.max(1, Math.floor((width + columnGap) / (minimumTileWidth + columnGap)));
        const rows = Math.max(1, Math.floor((height + rowGap) / (minimumTileHeight + rowGap)));
        const capacity = columns * rows;

        grid.style.setProperty('--product-grid-columns', columns);
        grid.style.setProperty('--product-grid-rows', rows);
        if (capacity === lastCapacity && columns === lastColumns && rows === lastRows) return;

        lastCapacity = capacity;
        lastColumns = columns;
        lastRows = rows;
        dotNetRef.invokeMethodAsync('SetProductGridCapacity', capacity, columns, rows)
            .catch(error => {
                if (!panel.isConnected) return;
                console.error('Could not update product grid capacity', error);
            });
    };

    const schedule = function () {
        if (frame) return;
        frame = window.requestAnimationFrame(measure);
    };

    const resizeObserver = new ResizeObserver(schedule);
    resizeObserver.observe(panel);
    const mutationObserver = new MutationObserver(schedule);
    mutationObserver.observe(panel, { childList: true, subtree: true });
    window.addEventListener('resize', schedule, { passive: true });
    panel._productGridCleanup = function () {
        resizeObserver.disconnect();
        mutationObserver.disconnect();
        window.removeEventListener('resize', schedule);
        if (frame) window.cancelAnimationFrame(frame);
        panel._productGridCleanup = null;
    };
    schedule();
};
window.restaurantOrders.stopProductGridObserver = function (panel) {
    if (panel && panel._productGridCleanup) panel._productGridCleanup();
};
window.restaurantOrders.downloadBase64 = function (fileName, contentType, base64) {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    const url = URL.createObjectURL(new Blob([bytes], { type: contentType }));
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
};
window.restaurantOrders.scrollOrderItemIntoView = function (elementId) {
    let attempts = 0;
    const reveal = function () {
        const item = document.getElementById(elementId);
        if (item) {
            item.scrollIntoView({ behavior: 'smooth', block: 'nearest', inline: 'nearest' });
            item.classList.add('order-item-focused');
            window.setTimeout(() => item.classList.remove('order-item-focused'), 1100);
            return;
        }
        if (++attempts < 10) window.requestAnimationFrame(reveal);
    };
    window.requestAnimationFrame(reveal);
};

window.playBeep = function () {
    try {
        const AudioCtx = window.AudioContext || window.webkitAudioContext;
        const ctx = new AudioCtx();

        function tone(freq, startTime, duration) {
            const osc = ctx.createOscillator();
            const gain = ctx.createGain();
            osc.connect(gain);
            gain.connect(ctx.destination);
            osc.frequency.value = freq;
            osc.type = 'sine';
            gain.gain.setValueAtTime(0.0001, startTime);
            gain.gain.exponentialRampToValueAtTime(0.25, startTime + 0.01);
            gain.gain.exponentialRampToValueAtTime(0.0001, startTime + duration);
            osc.start(startTime);
            osc.stop(startTime + duration);
        }

        const now = ctx.currentTime;
        tone(880, now, 0.12);
        tone(1175, now + 0.13, 0.15);
    } catch (e) {
        // Σιωπηλή αποτυχία (π.χ. παλιό browser) - δεν μπλοκάρει τίποτα
    }
};

// Ελεύθερο σύρσιμο τραπεζιών στην κάτοψη (admin). Χρησιμοποιεί pointer events +
// pointer capture (δουλεύει με ποντίκι ΚΑΙ αφή) αντί για το ασταθές HTML5 drag-and-drop.
window.initFloorplanDrag = function (canvasEl, dotNetRef) {
    if (!canvasEl) return;
    // Blazor enhanced navigation can reuse the canvas DOM node after the
    // component has been recreated. Replace old listeners so they never keep
    // a disposed DotNetObjectReference from the previous component instance.
    if (canvasEl._floorplanCleanup) canvasEl._floorplanCleanup();

    const reportError = function (err) {
        const message = err && err.message ? err.message : String(err || 'Άγνωστο σφάλμα');
        console.error('Floorplan save failed', err);
        dotNetRef.invokeMethodAsync('ReportFloorplanError', message)
            .catch(reportErr => console.error('Could not report floorplan error', reportErr));
    };

    const onPointerDown = function (e) {
        if (e.target.closest('.floor-table-remove')) return;
        const draggedEl = e.target.closest('.floor-table');
        if (!draggedEl || !canvasEl.contains(draggedEl)) return;

        e.preventDefault();
        e.stopPropagation();
        const tableRect = draggedEl.getBoundingClientRect();
        // Keep the same point of the table under the pointer. The saved x/y
        // values represent the table's center, because .floor-table uses
        // translate(-50%, -50%).
        const grabOffsetX = e.clientX - tableRect.left;
        const grabOffsetY = e.clientY - tableRect.top;
        const halfTableWidth = tableRect.width / 2;
        const halfTableHeight = tableRect.height / 2;
        const pointerStartX = e.clientX;
        const pointerStartY = e.clientY;
        let didMove = false;
        draggedEl.classList.add('dragging');
        draggedEl.setPointerCapture(e.pointerId);

        function onMove(ev) {
            if (Math.hypot(ev.clientX - pointerStartX, ev.clientY - pointerStartY) > 4) didMove = true;
            const rect = canvasEl.getBoundingClientRect();
            let x = ev.clientX - rect.left - grabOffsetX + halfTableWidth;
            let y = ev.clientY - rect.top - grabOffsetY + halfTableHeight;
            // Clamp the center by half the tile size, so the full table stays
            // inside the floorplan rather than snapping/clipping at a corner.
            x = Math.max(halfTableWidth, Math.min(rect.width - halfTableWidth, x));
            y = Math.max(halfTableHeight, Math.min(rect.height - halfTableHeight, y));

            const xPct = (x / rect.width) * 100;
            const yPct = (y / rect.height) * 100;

            draggedEl.style.left = xPct + '%';
            draggedEl.style.top = yPct + '%';
            draggedEl.dataset.pendingX = xPct;
            draggedEl.dataset.pendingY = yPct;
        }

        function onUp(ev) {
            try { draggedEl.releasePointerCapture(ev.pointerId); } catch (err) { }
            draggedEl.removeEventListener('pointermove', onMove);
            draggedEl.removeEventListener('pointerup', onUp);
            draggedEl.removeEventListener('pointercancel', onUp);
            draggedEl.classList.remove('dragging');
            if (didMove) {
                canvasEl.dataset.dragClickSuppressed = '1';
                setTimeout(() => { delete canvasEl.dataset.dragClickSuppressed; }, 0);
            }

            const tableId = draggedEl.getAttribute('data-table-id');
            const xPct = draggedEl.dataset.pendingX;
            const yPct = draggedEl.dataset.pendingY;
            delete draggedEl.dataset.pendingX;
            delete draggedEl.dataset.pendingY;

            if (tableId && xPct !== undefined && yPct !== undefined) {
                dotNetRef.invokeMethodAsync('SavePosition', parseInt(tableId, 10), parseFloat(xPct), parseFloat(yPct))
                    .catch(reportError);
            }
        }

        draggedEl.addEventListener('pointermove', onMove);
        draggedEl.addEventListener('pointerup', onUp);
        draggedEl.addEventListener('pointercancel', onUp);
    };

    // Τοποθέτηση από τη λίστα: χειρίζεται το click εδώ, με τις πραγματικές
    // διαστάσεις canvas, και στέλνει απευθείας τη θέση στον .NET handler.
    const onClick = function (e) {
        if (canvasEl.dataset.dragClickSuppressed === '1') {
            e.preventDefault();
            e.stopPropagation();
            delete canvasEl.dataset.dragClickSuppressed;
            return;
        }
        if (e.target.closest('.floor-table-remove')) return;
        const tableId = canvasEl.dataset.placementTableId;
        if (!tableId) return;
        const point = window.getFloorplanPosition(canvasEl, e.clientX, e.clientY, canvasEl.dataset.placementTableName || '');
        e.preventDefault();
        e.stopPropagation();
        dotNetRef.invokeMethodAsync('PlaceSelectedTable', parseInt(tableId, 10), point[0], point[1])
            .catch(reportError);
    };

    canvasEl.addEventListener('pointerdown', onPointerDown);
    canvasEl.addEventListener('click', onClick);
    canvasEl._floorplanCleanup = function () {
        canvasEl.removeEventListener('pointerdown', onPointerDown);
        canvasEl.removeEventListener('click', onClick);
        delete canvasEl._floorplanCleanup;
    };
};

// Convert a canvas click to percentages using its actual rendered dimensions
// (not the nominal CSS width, which changes with browser zoom/responsive layout).
window.getFloorplanPosition = function (canvasEl, clientX, clientY, tableName) {
    if (!canvasEl) return [50, 50];
    const rect = canvasEl.getBoundingClientRect();
    const probe = document.createElement('div');
    probe.className = 'floor-table';
    probe.textContent = tableName || '';
    Object.assign(probe.style, { position: 'fixed', left: '-10000px', top: '-10000px', transform: 'none', visibility: 'hidden' });
    document.body.appendChild(probe);
    const tile = probe.getBoundingClientRect();
    probe.remove();
    const halfW = tile.width / 2;
    const halfH = tile.height / 2;
    const x = Math.max(halfW, Math.min(rect.width - halfW, clientX - rect.left));
    const y = Math.max(halfH, Math.min(rect.height - halfH, clientY - rect.top));
    return [(x / rect.width) * 100, (y / rect.height) * 100];
};


// Mobile browser recovery: when Safari/Chrome restores this page from the
// back-forward cache, the HTML may return while the Blazor Server circuit is dead.
// Reload only for a BFCache restoration, not on every tab switch, to avoid losing an order in progress.
(function () {
    window.addEventListener('pageshow', function (event) {
        if (event && event.persisted) {
            window.location.reload();
        }
    });
})();

// Make layout tile drags carry data so Chromium/Firefox accept the drop target.
document.addEventListener('dragstart', function (event) {
    const handle = event.target && event.target.closest
        ? event.target.closest('.tile-drag-handle[data-layout-id]')
        : null;
    if (!handle || !event.dataTransfer) return;
    event.dataTransfer.setData('text/plain', `${handle.dataset.layoutKind || 'tile'}:${handle.dataset.layoutId}`);
    event.dataTransfer.effectAllowed = 'move';
});

// Keep the compact POS navigation usable even if the Blazor Server circuit is
// reconnecting. Opening/closing the menu is presentation-only and should not
// need a round trip to the cloud server.
document.addEventListener('click', function (event) {
    const target = event.target;
    if (!(target instanceof Element)) return;

    const adminToggle = target.closest('[data-admin-sidebar-toggle="true"]');
    if (adminToggle) {
        const root = adminToggle.closest('.topbar');
        const sidebar = root?.querySelector('.admin-sidebar');
        const backdrop = root?.querySelector('[data-admin-sidebar-backdrop="true"]');
        if (!sidebar) return;
        const isOpen = sidebar.classList.toggle('is-open');
        backdrop?.classList.toggle('is-open', isOpen);
        adminToggle.setAttribute('aria-expanded', String(isOpen));
        document.body.classList.toggle('admin-menu-open', isOpen);
        return;
    }

    if (target.closest('[data-admin-sidebar-backdrop="true"]')) {
        document.querySelectorAll('.topbar').forEach(root => {
            root.querySelector('.admin-sidebar')?.classList.remove('is-open');
            root.querySelector('[data-admin-sidebar-backdrop="true"]')?.classList.remove('is-open');
            root.querySelector('[data-admin-sidebar-toggle="true"]')?.setAttribute('aria-expanded', 'false');
        });
        document.body.classList.remove('admin-menu-open');
        return;
    }

    const toggle = target.closest('[data-nav-toggle="true"]');
    if (toggle) {
        const nav = toggle.closest('nav');
        const links = nav?.querySelector('.nav-links');
        if (!links) return;
        const isOpen = links.classList.toggle('is-open');
        toggle.setAttribute('aria-expanded', String(isOpen));
        return;
    }

    if (target.closest('.topbar .nav-links a')) {
        document.querySelectorAll('.topbar nav').forEach(nav => {
            nav.querySelector('.nav-links')?.classList.remove('is-open');
            nav.querySelector('[data-nav-toggle="true"]')?.setAttribute('aria-expanded', 'false');
        });
    }

    if (target.closest('.admin-sidebar a')) {
        document.querySelectorAll('.topbar').forEach(root => {
            root.querySelector('.admin-sidebar')?.classList.remove('is-open');
            root.querySelector('[data-admin-sidebar-backdrop="true"]')?.classList.remove('is-open');
            root.querySelector('[data-admin-sidebar-toggle="true"]')?.setAttribute('aria-expanded', 'false');
        });
        document.body.classList.remove('admin-menu-open');
    }
});

document.addEventListener('keydown', function (event) {
    if (event.key !== 'Escape') return;
    document.querySelectorAll('.topbar').forEach(root => {
        root.querySelector('.admin-sidebar')?.classList.remove('is-open');
        root.querySelector('[data-admin-sidebar-backdrop="true"]')?.classList.remove('is-open');
        root.querySelector('[data-admin-sidebar-toggle="true"]')?.setAttribute('aria-expanded', 'false');
    });
    document.body.classList.remove('admin-menu-open');
});


// Native Android network-printer bridge. A browser cannot open raw TCP sockets;
// the RestaurantOrders Android WebView sends ESC/POS directly over the shop LAN.
window.restaurantOrdersAndroidPrinter = {
    isAvailable: () => {
        const bridge = window.RestaurantOrdersAndroidNative || window.RestaurantOrdersSunmiNative;
        return !!bridge && typeof bridge.printTcpRawBase64 === 'function';
    },
    printTcpRaw: (ipAddress, port, base64, copies) => {
        const bridge = window.RestaurantOrdersAndroidNative || window.RestaurantOrdersSunmiNative;
        if (typeof bridge?.printTcpRawBase64 !== 'function') return 'unavailable';

        const response = JSON.parse(bridge.printTcpRawBase64(
            String(ipAddress || ''), Number(port) || 0, String(base64 || ''), Number(copies) || 1));
        if (!response.ok) throw new Error(response.error || 'Απέτυχε η τοπική εκτύπωση ESC/POS από το Android.');
        return 'printed';
    }
};

// A Windows browser cannot open raw TCP sockets. When the full Windows POS is
// installed on this same computer, its loopback endpoint sends the ESC/POS job
// to the shop printer (TCP) or to the installed Windows printer queue (USB).
window.restaurantOrdersWindowsPrinter = (() => {
    const endpoint = 'http://127.0.0.1:5080/api/local-print';

    return {
        isAvailable: async () => {
            try {
                const response = await fetch(`${endpoint}/health`, {
                    method: 'GET',
                    mode: 'cors',
                    targetAddressSpace: 'loopback',
                    credentials: 'omit',
                    cache: 'no-store'
                });
                return response.ok;
            } catch {
                return false;
            }
        },
        printRaw: async (connectionType, ipAddress, port, printerName, dataBase64, copies) => {
            let response;
            try {
                response = await fetch(`${endpoint}/raw`, {
                    method: 'POST',
                    mode: 'cors',
                    targetAddressSpace: 'loopback',
                    credentials: 'omit',
                    cache: 'no-store',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        connectionType: String(connectionType || 'Tcp'),
                        ipAddress: String(ipAddress || ''),
                        port: Number(port) || 0,
                        printerName: String(printerName || ''),
                        dataBase64: String(dataBase64 || ''),
                        copies: Number(copies) || 1
                    })
                });
            } catch (error) {
                throw new Error('Δεν συνδέθηκε η Cloud σελίδα με την τοπική υπηρεσία εκτύπωσης των Windows. Έλεγξε ότι είναι ενεργή και επέτρεψε την πρόσβαση στο τοπικό δίκτυο στον browser.');
            }

            const result = await response.json().catch(() => ({}));
            if (!response.ok || !result.ok)
                throw new Error(result.error || `Η τοπική υπηρεσία Windows επέστρεψε σφάλμα (${response.status}).`);
            return 'printed';
        }
    };
})();

window.sunmiPrinter = (() => {
    const embedded = window.parent && window.parent !== window;
    const pending = new Map();

    if (embedded) {
        window.addEventListener('message', event => {
            if (event.source !== window.parent || event.data?.source !== 'restaurantorders-sunmi-host') return;
            const waiter = pending.get(String(event.data.requestId || ''));
            if (!waiter) return;
            pending.delete(String(event.data.requestId));
            clearTimeout(waiter.timer);
            if (event.data.ok) waiter.resolve(true);
            else waiter.reject(new Error(event.data.error || 'Η υπηρεσία SUNMI δεν ολοκλήρωσε την εκτύπωση.'));
        });
    }

    function requestHost(action, payload = {}) {
        const nativeBridge = window.RestaurantOrdersAndroidNative || window.RestaurantOrdersSunmiNative;
        if (nativeBridge) {
            try {
                const raw = action === 'prepare'
                    ? nativeBridge.prepare()
                    : nativeBridge.printRawBase64(String(payload.base64 || ''), Number(payload.copies) || 1);
                const result = JSON.parse(raw || '{}');
                if (result.ok) return Promise.resolve(true);
                return Promise.reject(new Error(result.error || 'Η υπηρεσία SUNMI δεν ολοκλήρωσε την εκτύπωση.'));
            } catch (error) {
                return Promise.reject(error instanceof Error ? error : new Error(String(error)));
            }
        }
        if (!embedded)
            return Promise.reject(new Error('Για τον ενσωματωμένο εκτυπωτή, άνοιξε το RestaurantOrders από την εφαρμογή RestaurantOrders σε συσκευή SUNMI.'));
        const requestId = `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                pending.delete(requestId);
                reject(new Error('Δεν απάντησε η γέφυρα εκτύπωσης SUNMI. Άνοιξε την εφαρμογή RestaurantOrders σε συσκευή SUNMI και ξαναδοκίμασε.'));
            }, action === 'printRaw' ? 45000 : 15000);
            pending.set(requestId, { resolve, reject, timer });
            window.parent.postMessage({ source: 'restaurantorders-sunmi', requestId, action, ...payload }, '*');
        });
    }

    return {
        prepare: () => requestHost('prepare'),
        printRaw: (base64, copies) => requestHost('printRaw', { base64, copies })
    };
})();
