// Wiring: state, input, undo, save, create, delete, duplicate.

import { api, loadStoredToken, setToken, clearToken } from './api.js';
import { View, DEFAULT_FACET, BRITAIN } from './view.js';
import {
    LAYERS, draw, drawEntities, drawDraft, hitTest, pick, geometryOf, applyGeometry, moveShape,
    resizeRect, moveNode
} from './shapes.js';
import { TOOLS, askFor, loadTypeLists, fillRouteList, fillList } from './tools.js';
import { OVERLAY_LAYERS, emptyOverlays, fetchOverlays, counts, draw as drawOverlays } from './overlays.js';

const ENTITY_POLL_MS = 1500;

const $ = (id) => document.getElementById(id);

const dom = {
    gate: $('token-gate'), form: $('token-form'), tokenInput: $('token-input'), tokenError: $('token-error'),
    workspace: $('workspace'), canvas: $('map'), facet: $('facet'),
    toolbar: $('toolbar'), menu: $('context-menu'),
    filter: $('filter'), matches: $('matches'),
    layers: $('layers'), overlayLayers: $('overlay-layers'),
    properties: $('properties'), coords: $('coords'), hint: $('hint'), status: $('status'),
    banner: $('banner'), bannerText: $('banner-text'),
    bannerDiscard: $('banner-discard'), bannerDismiss: $('banner-dismiss')
};

const state = {
    facets: [],
    shapes: [],
    entities: [],
    overlays: emptyOverlays(),
    visible: new Set(['zones', 'dailylife', 'spawners', 'entities']),
    overlayVisible: new Set(),
    selected: null,
    hovered: null,
    // Shapes edited since the last save, so a save sends only what changed.
    dirty: new Set(),
    // Session undo: each entry restores one shape's geometry to what it was before a change.
    undo: [],
    // The server's value for each shape at the moment it first became dirty. This is what makes
    // "discard" able to put the FILE back, not just the view - which matters because a save whose
    // reload was rejected has already written to disk.
    baseline: new Map(),
    drag: null,
    spaceDown: false,
    tool: null,
    draft: null,
    // Shapes matching the filter, or null when the filter is empty.
    matches: null
};

const view = new View(dom.canvas);
view.onTileLoaded = () => render();

// Exposed deliberately. This is a localhost-only admin tool with nothing on the page a same-origin
// script could not already reach, and having the real state reachable is what lets the editor be
// driven and asserted on from a browser automation script instead of by squinting at screenshots.
window.__editor = { state, view };

// --- startup ---------------------------------------------------------------------------------

dom.form.addEventListener('submit', async (event) => {
    event.preventDefault();
    await connect(dom.tokenInput.value.trim(), true);
});

const stored = loadStoredToken();

if (stored) {
    connect(stored, false);
}

async function connect(token, remember) {
    if (!token) {
        return;
    }

    setToken(token, remember);

    try {
        const status = await api.status();

        state.facets = status.facets;

        dom.gate.hidden = true;
        dom.workspace.hidden = false;

        buildFacetPicker();
        buildLayerList();
        buildOverlayList();

        fillList('facet-list', state.facets.map((f) => f.name));

        // Size the canvas before choosing a view: setFacet's scale clamp reads the canvas box, and
        // the workspace has only just become visible.
        view.resize();
        showFacet(state.facets.find((f) => f.name === DEFAULT_FACET) || state.facets[0]);

        await refreshShapes({ force: true });
        loadTypeLists(api);
        pollEntities();
        render();
    } catch (error) {
        // A stored token that no longer works should not lock the user out silently.
        clearToken();
        dom.gate.hidden = false;
        dom.workspace.hidden = true;
        dom.tokenError.textContent = error.status === 401 ? 'Token rejected.' : error.message;
    }
}

function buildFacetPicker() {
    dom.facet.innerHTML = '';

    for (const facet of state.facets) {
        const option = document.createElement('option');
        option.value = facet.name;
        option.textContent = facet.name;
        dom.facet.append(option);
    }

    dom.facet.addEventListener('change', () => {
        showFacet(state.facets.find((f) => f.name === dom.facet.value));
    });
}

/** Keeps the picker and the view on the same facet. */
function showFacet(facet) {
    view.setFacet(facet);
    dom.facet.value = facet.name;
    cancelTool();
    select(null);
    loadOverlays(facet.name);
    render();
}

async function loadOverlays(mapName) {
    state.overlays = await fetchOverlays(api, mapName);
    updateCounts();
    render();
}

function buildLayerList() {
    dom.layers.innerHTML = '';

    for (const [key, layer] of Object.entries(LAYERS)) {
        dom.layers.append(layerRow(key, layer.label, layer.color, state.visible));
    }
}

function buildOverlayList() {
    dom.overlayLayers.innerHTML = '';

    for (const [key, layer] of Object.entries(OVERLAY_LAYERS)) {
        dom.overlayLayers.append(layerRow(key, layer.label, layer.color, state.overlayVisible));
    }
}

function layerRow(key, label, color, set) {
    const item = document.createElement('li');
    const row = document.createElement('label');
    const checkbox = document.createElement('input');

    checkbox.type = 'checkbox';
    checkbox.checked = set.has(key);
    checkbox.dataset.toggle = key;
    checkbox.addEventListener('change', () => {
        checkbox.checked ? set.add(key) : set.delete(key);

        if (state.selected && !checkbox.checked && state.selected.layer === key) {
            select(null);
        }

        render();
    });

    const swatch = document.createElement('span');
    swatch.className = 'swatch';
    swatch.style.background = color;

    const count = document.createElement('span');
    count.className = 'count';
    count.dataset.layer = key;

    row.append(checkbox, swatch, document.createTextNode(label), count);
    item.append(row);

    return item;
}

function updateCounts() {
    const overlayCounts = counts(state.overlays);

    for (const element of dom.layers.querySelectorAll('.count')) {
        const layer = element.dataset.layer;

        element.textContent = layer === 'entities'
            ? state.entities.filter((e) => e.map === view.facet.name).length
            : state.shapes.filter((s) => s.layer === layer && s.map === view.facet.name).length;
    }

    for (const element of dom.overlayLayers.querySelectorAll('.count')) {
        element.textContent = overlayCounts[element.dataset.layer] ?? 0;
    }
}

// --- data ------------------------------------------------------------------------------------

/**
 * Replaces the local shapes with the server's.
 *
 * Refuses to run while there are unsaved edits unless forced. This is the guard the silent-revert
 * bug needed: a refresh over a dirty shape throws away work with no trace, and the user cannot tell
 * that from a save that did not apply.
 */
async function refreshShapes({ force = false } = {}) {
    if (state.dirty.size > 0 && !force) {
        return false;
    }

    const response = await api.shapes();

    state.shapes = response.shapes;
    state.dirty.clear();
    state.baseline.clear();
    state.undo.length = 0;
    state.selected = null;

    fillRouteList(state.shapes);
    showProperties(null);
    applyFilter();
    updateCounts();
    updateToolbar();

    return true;
}

async function pollEntities() {
    if (state.visible.has('entities')) {
        try {
            const response = await api.entities();
            state.entities = response.entities;
            updateCounts();
            render();
        } catch {
            // A dropped poll is not worth a message; the next one will say so if it persists.
        }
    }

    setTimeout(pollEntities, ENTITY_POLL_MS);
}

// --- rendering -------------------------------------------------------------------------------

let pending = false;

function render() {
    if (pending) {
        return;
    }

    pending = true;

    requestAnimationFrame(() => {
        pending = false;

        if (!view.facet) {
            return;
        }

        view.drawMap();
        drawOverlays(view.ctx, view, state.overlays, state.overlayVisible);
        draw(view.ctx, view, state.shapes, state.visible, state.selected, state.hovered, state.matches);

        if (state.visible.has('entities')) {
            drawEntities(view.ctx, view, state.entities);
        }

        drawDraft(view.ctx, view, state.draft);
    });
}

new ResizeObserver(() => {
    if (view.facet && view.resize()) {
        render();
    }
}).observe(dom.canvas);

function watchPixelRatio() {
    const query = matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`);

    query.addEventListener('change', () => {
        if (view.facet) {
            view.resize();
            render();
        }

        watchPixelRatio();
    }, { once: true });
}

watchPixelRatio();

// --- toolbar ---------------------------------------------------------------------------------

dom.toolbar.addEventListener('click', (event) => {
    const action = event.target.dataset?.act;

    if (!action) {
        return;
    }

    const midX = dom.canvas.clientWidth / 2;
    const midY = dom.canvas.clientHeight / 2;

    switch (action) {
        case 'zoom-in': view.zoomAt(midX, midY, 1.5); render(); break;
        case 'zoom-out': view.zoomAt(midX, midY, 1 / 1.5); render(); break;
        case 'whole-map': view.fitAll(); render(); break;
        case 'britain': view.goTo(BRITAIN.x, BRITAIN.y, 2); render(); break;
        case 'goto': gotoCoordinate(); break;
        case 'add': openAddMenu(event.target); break;
        case 'duplicate': duplicateSelected(); break;
        case 'delete': deleteSelected(); break;
        case 'save': save(); break;
        case 'discard': discard(); break;
    }
});

function updateToolbar() {
    const hasSelection = state.selected !== null;
    const hasEdits = state.dirty.size > 0;

    dom.toolbar.querySelector('[data-act="duplicate"]').disabled = !hasSelection;
    dom.toolbar.querySelector('[data-act="delete"]').disabled = !hasSelection;
    dom.toolbar.querySelector('[data-act="save"]').disabled = !hasEdits;
    dom.toolbar.querySelector('[data-act="discard"]').disabled = !hasEdits;
}

async function gotoCoordinate() {
    const values = await askFor({
        title: 'Go to coordinate',
        submit: 'Go',
        fields: [
            { key: 'x', label: 'X', required: true, value: String(Math.round(view.centerX)) },
            { key: 'y', label: 'Y', required: true, value: String(Math.round(view.centerY)) }
        ]
    });

    if (!values) {
        return;
    }

    const x = Number(values.x);
    const y = Number(values.y);

    if (!Number.isFinite(x) || !Number.isFinite(y)) {
        setStatus('That is not a coordinate.', 'error');
        return;
    }

    view.goTo(x, y, Math.max(view.scale, 2));
    render();
}

// --- context menu ------------------------------------------------------------------------------

dom.canvas.addEventListener('contextmenu', (event) => {
    event.preventDefault();

    const [worldX, worldY] = view.toWorld(event.offsetX, event.offsetY);
    const hit = pick(view, state.shapes, state.visible, worldX, worldY);

    if (hit) {
        select(hit);
        render();
        showMenu(event.clientX, event.clientY, shapeMenu(hit));
        return;
    }

    showMenu(event.clientX, event.clientY, placeMenu(Math.floor(worldX), Math.floor(worldY)));
});

function shapeMenu(shape) {
    return [
        { heading: shape.label || shape.id },
        { label: 'Properties', run: () => dom.properties.querySelector('input:not([readonly])')?.focus() },
        { label: 'Duplicate', run: duplicateSelected },
        { label: 'Delete', run: deleteSelected },
        { divider: true },
        { label: 'Center on', run: () => centerOnShape(shape) }
    ];
}

function placeMenu(x, y) {
    // Placing from the menu skips the tool's click phase - the click that opened the menu already
    // said where.
    const at = (key) => () => startTool(key, [x, y, 0]);

    return [
        { heading: `${x}, ${y}` },
        { label: 'Add spawner here', run: at('spawner') },
        { label: 'Add zone here', run: at('zone') },
        { label: 'Add watch post here', run: at('watchpost') },
        { label: 'Add townsfolk here', run: at('townsfolk') }
    ];
}

function openAddMenu(button) {
    const box = button.getBoundingClientRect();

    showMenu(
        box.left,
        box.bottom + 4,
        Object.entries(TOOLS).map(([key, tool]) => ({ label: tool.label, run: () => startTool(key) }))
    );
}

function showMenu(x, y, items) {
    dom.menu.innerHTML = '';

    for (const item of items) {
        const element = document.createElement('li');

        if (item.divider) {
            element.className = 'divider';
        } else if (item.heading) {
            element.className = 'heading';
            element.textContent = item.heading;
        } else {
            element.textContent = item.label;
            element.dataset.menu = item.label;
            element.addEventListener('click', () => {
                hideMenu();
                item.run();
            });
        }

        dom.menu.append(element);
    }

    dom.menu.hidden = false;

    // Keep it on screen when opened near an edge.
    const box = dom.menu.getBoundingClientRect();
    dom.menu.style.left = `${Math.min(x, window.innerWidth - box.width - 8)}px`;
    dom.menu.style.top = `${Math.min(y, window.innerHeight - box.height - 8)}px`;
}

function hideMenu() {
    dom.menu.hidden = true;
}

window.addEventListener('mousedown', (event) => {
    if (!dom.menu.hidden && !dom.menu.contains(event.target)) {
        hideMenu();
    }
}, true);

function centerOnShape(shape) {
    const [x, y] = shape.kind === 'rect'
        ? [shape.rect[0] + shape.rect[2] / 2, shape.rect[1] + shape.rect[3] / 2]
        : shape.points[0];

    if (shape.map !== view.facet.name) {
        const facet = state.facets.find((f) => f.name === shape.map);

        if (facet) {
            showFacet(facet);
        }
    }

    view.goTo(x, y, Math.max(view.scale, 2));
    render();
}

// --- filter ------------------------------------------------------------------------------------

dom.filter.addEventListener('input', () => {
    applyFilter();
    render();
});

function applyFilter() {
    const term = dom.filter.value.trim().toLowerCase();

    if (!term) {
        state.matches = null;
        dom.matches.innerHTML = '';
        return;
    }

    const found = state.shapes.filter(
        (shape) => state.visible.has(shape.layer)
            && `${shape.label ?? ''} ${shape.id}`.toLowerCase().includes(term)
    );

    state.matches = new Set(found);
    dom.matches.innerHTML = '';

    for (const shape of found) {
        const item = document.createElement('li');
        const facet = document.createElement('span');

        facet.className = 'layer';
        facet.textContent = ` ${shape.map}`;

        item.textContent = shape.label || shape.id;
        item.dataset.match = shape.id;
        item.append(facet);
        item.addEventListener('click', () => {
            select(shape);
            centerOnShape(shape);
        });

        dom.matches.append(item);
    }

    if (found.length === 0) {
        const empty = document.createElement('li');
        empty.className = 'layer';
        empty.textContent = 'No matches';
        dom.matches.append(empty);
    }
}

// --- create tools ------------------------------------------------------------------------------

function startTool(key, placeAt = null) {
    cancelTool();

    const tool = TOOLS[key];

    state.tool = { key, ...tool, phase: 0 };
    state.draft = { kind: tool.kind === 'rect' ? 'rect' : 'points', points: [], rect: null };

    select(null);

    // No geometry to collect, or the context menu already said where.
    if (tool.kind === 'form') {
        completeTool();
        return;
    }

    if (placeAt && tool.kind === 'point') {
        state.draft.points = [placeAt];
        completeTool();
        return;
    }

    if (placeAt && tool.kind === 'rect') {
        // A default box centred on the click, rather than demanding a drag straight away. It can be
        // resized on the canvas afterwards like any other rectangle.
        state.draft.rect = [placeAt[0] - 8, placeAt[1] - 8, 16, 16];
        completeTool();
        return;
    }

    dom.hint.textContent = tool.hint;
    render();
}

function cancelTool() {
    state.tool = null;
    state.draft = null;
    dom.hint.textContent = '';
    render();
}

/** A canvas click while a tool is active. Returns true when the tool consumed it. */
function toolClick(worldX, worldY) {
    const tool = state.tool;

    if (!tool) {
        return false;
    }

    const point = [Math.floor(worldX), Math.floor(worldY), 0];

    if (tool.kind === 'point') {
        state.draft.points = [point];
        completeTool();
        return true;
    }

    if (tool.kind === 'route') {
        state.draft.points.push(point);
        render();
        return true;
    }

    if (tool.kind === 'point-then-route') {
        if (tool.phase === 0) {
            state.draft.points = [point];
            tool.phase = 1;
            dom.hint.textContent = tool.hint2;
        } else {
            state.draft.points.push(point);
        }

        render();
        return true;
    }

    return false;
}

function finishTool() {
    const tool = state.tool;

    if (!tool) {
        return;
    }

    if (tool.kind === 'route' && state.draft.points.length < 2) {
        setStatus('A route needs at least two nodes.', 'error');
        return;
    }

    if (tool.kind === 'point-then-route' && state.draft.points.length < 2) {
        setStatus('A shop needs a location and at least one walk-home node.', 'error');
        return;
    }

    completeTool();
}

async function completeTool() {
    const tool = state.tool;
    const draft = state.draft;

    const values = await askFor(tool);

    if (!values) {
        cancelTool();
        return;
    }

    const request = {
        kind: tool.key,
        layer: tool.layer,
        map: view.facet.name,
        name: values.name || null,
        props: {}
    };

    for (const [key, value] of Object.entries(values)) {
        if (key !== 'name' && value !== '') {
            request.props[key] = value;
        }
    }

    if (tool.kind === 'rect') {
        request.rect = draft.rect;
    } else if (tool.kind === 'point' || tool.kind === 'route') {
        request.points = draft.points;
    } else if (tool.kind === 'point-then-route') {
        request.points = [draft.points[0]];
        request.props.homeRoute = draft.points.slice(1).map(([x, y, z]) => ({ x, y, z }));
    }

    if (tool.key === 'spawner') {
        request.file = values.file;
    }

    cancelTool();

    try {
        await api.create(request);
    } catch (error) {
        showBanner(`Could not create the ${tool.label.toLowerCase()}: ${error.message}`);
        return;
    }

    await reloadAndRefresh([tool.layer], `${tool.label} created.`);
}

// --- duplicate and delete ------------------------------------------------------------------------

async function duplicateSelected() {
    const shape = state.selected;

    if (!shape) {
        return;
    }

    try {
        await api.duplicate({ layer: shape.layer, file: shape.file, pointer: shape.pointer });
    } catch (error) {
        showBanner(`Could not duplicate "${shape.label}": ${error.message}`);
        return;
    }

    await reloadAndRefresh([shape.layer], `Duplicated ${shape.label}.`);
}

async function deleteSelected() {
    const shape = state.selected;

    if (!shape) {
        return;
    }

    const isShop = shape.id.startsWith('shop:');
    const extra = isShop
        ? '\n\nThe shopkeeper stays in the world and will be sent back to its shop on reload.'
        : '';

    if (!confirm(`Delete "${shape.label}"?${extra}`)) {
        return;
    }

    try {
        await api.remove({ layer: shape.layer, file: shape.file, pointer: shape.pointer });
    } catch (error) {
        showBanner(`Could not delete "${shape.label}": ${error.message}`);
        return;
    }

    await reloadAndRefresh([shape.layer], `Deleted ${shape.label}.`);
}

// --- saving --------------------------------------------------------------------------------------

async function save() {
    if (state.dirty.size === 0) {
        return;
    }

    const edits = [];
    const layers = new Set();

    for (const shape of state.dirty) {
        edits.push({
            layer: shape.layer,
            file: shape.file,
            pointer: shape.pointer,
            propsPointer: shape.propsPointer,
            rect: shape.kind === 'rect' ? shape.rect : undefined,
            points: shape.kind === 'rect' ? undefined : shape.points,
            props: propsPayload(shape)
        });

        layers.add(shape.layer);
    }

    setStatus('Saving...');

    try {
        await api.patch(edits);
    } catch (error) {
        // Nothing was written. Keep the edits so they can be corrected and saved again.
        showBanner(`Save failed, nothing was written: ${error.message}`);
        return;
    }

    await reloadAndRefresh([...layers], 'Saved and reloaded.');
}

/** Property values keyed by the path the server writes them to, not by the display key. */
function propsPayload(shape) {
    if (!shape.fields || !shape.editedProps) {
        return undefined;
    }

    const payload = {};

    for (const field of shape.fields) {
        if (field.key in shape.editedProps) {
            payload[field.path || field.key] = String(shape.props[field.key] ?? '');
        }
    }

    return Object.keys(payload).length > 0 ? payload : undefined;
}

/**
 * Reloads the systems whose files changed, then re-reads the shapes.
 *
 * The awkward case is a reload that fails validation: the file IS on disk, but the server is still
 * running the previous config, so re-reading the shapes would show the OLD values and look exactly
 * like the save being undone. That is the bug that lost work silently. Instead: say plainly that
 * the file was written but rejected, keep the local edits so they can be corrected, and do not
 * refresh over them.
 */
async function reloadAndRefresh(layers, successMessage) {
    for (const layer of layers) {
        const system = LAYERS[layer].reload;

        if (!system) {
            continue;
        }

        try {
            await api.reload(system);
        } catch (error) {
            updateToolbar();

            showBanner(
                `Written to disk, but the server refused to load it, so the shard is still running `
                + `the previous ${system} config:\n\n${error.message}\n\n`
                + `Your edits are still here - fix them and save again, or discard them to go back `
                + `to what the server has.`,
                'warn'
            );

            return false;
        }
    }

    hideBanner();
    await refreshShapes({ force: true });

    if (state.overlayVisible.size > 0) {
        await loadOverlays(view.facet.name);
    }

    render();
    setStatus(successMessage, 'ok');

    return true;
}

/**
 * Puts everything back the way the server has it - including the file.
 *
 * Just re-reading the shapes would not be enough. A save whose reload was rejected has already
 * written to disk, so the file and the running config disagree; dropping the local edits alone
 * would leave that bad file in place to fail on the next restart. Writing the baselines back is
 * what makes "discard" mean discard.
 */
async function discard() {
    const rollback = [];
    const layers = new Set();

    for (const [shape, snap] of state.baseline) {
        rollback.push({
            layer: shape.layer,
            file: shape.file,
            pointer: shape.pointer,
            propsPointer: shape.propsPointer,
            rect: snap.geometry?.rect,
            points: snap.geometry?.points,
            props: snap.props
        });

        layers.add(shape.layer);
    }

    hideBanner();
    state.dirty.clear();
    state.baseline.clear();

    if (rollback.length > 0) {
        try {
            await api.patch(rollback);

            for (const layer of layers) {
                const system = LAYERS[layer].reload;

                if (system) {
                    await api.reload(system);
                }
            }
        } catch (error) {
            showBanner(`Could not restore the file to the server's version: ${error.message}`);
            return;
        }
    }

    await refreshShapes({ force: true });
    render();
    setStatus('Edits discarded and the file restored.', 'ok');
}

// --- banner --------------------------------------------------------------------------------------

function showBanner(message, kind) {
    dom.bannerText.textContent = message;
    dom.banner.className = kind === 'warn' ? 'warn' : '';
    dom.banner.hidden = false;
    dom.bannerDiscard.hidden = state.dirty.size === 0;
}

function hideBanner() {
    dom.banner.hidden = true;
}

dom.bannerDismiss.addEventListener('click', hideBanner);
dom.bannerDiscard.addEventListener('click', discard);

// --- input -----------------------------------------------------------------------------------

dom.canvas.addEventListener('mousedown', (event) => {
    hideMenu();

    // Middle button, or space held: always a pan, whatever is under the cursor.
    if (event.button === 1 || state.spaceDown) {
        event.preventDefault();
        beginPan(event);
        return;
    }

    if (event.button !== 0) {
        return;
    }

    const [worldX, worldY] = view.toWorld(event.offsetX, event.offsetY);

    if (state.tool && state.tool.kind === 'rect') {
        const x = Math.floor(worldX);
        const y = Math.floor(worldY);

        state.drag = { kind: 'draw-rect', startX: x, startY: y };
        state.draft.rect = [x, y, 1, 1];
        render();
        return;
    }

    if (toolClick(worldX, worldY)) {
        return;
    }

    const hit = hitTest(view, state.shapes, state.visible, state.selected, worldX, worldY);

    // Only the already-selected shape can be dragged. Anything else selects and pans, which is what
    // makes a rectangle covering half of Britain something you can still navigate across.
    if (hit && hit.shape === state.selected) {
        state.drag = {
            kind: hit.mode,
            index: hit.index,
            shape: hit.shape,
            before: geometryOf(hit.shape),
            originX: worldX,
            originY: worldY,
            moved: false
        };

        render();
        return;
    }

    select(hit ? hit.shape : null);
    beginPan(event);
    render();
});

function beginPan(event) {
    state.drag = { kind: 'pan', lastX: event.clientX, lastY: event.clientY };
    dom.canvas.classList.add('dragging');
}

dom.canvas.addEventListener('dblclick', (event) => {
    if (state.tool) {
        event.preventDefault();
        finishTool();
    }
});

window.addEventListener('mousemove', (event) => {
    const rect = dom.canvas.getBoundingClientRect();
    const [worldX, worldY] = view.toWorld(event.clientX - rect.left, event.clientY - rect.top);

    if (view.facet) {
        dom.coords.textContent = `${Math.floor(worldX)}, ${Math.floor(worldY)}`;
    }

    const drag = state.drag;

    if (!drag) {
        updateHover(event, worldX, worldY);
        return;
    }

    if (drag.kind === 'pan') {
        view.panBy(event.clientX - drag.lastX, event.clientY - drag.lastY);
        drag.lastX = event.clientX;
        drag.lastY = event.clientY;
        render();
        return;
    }

    if (drag.kind === 'draw-rect') {
        const x = Math.floor(worldX);
        const y = Math.floor(worldY);

        state.draft.rect = [
            Math.min(drag.startX, x),
            Math.min(drag.startY, y),
            Math.max(1, Math.abs(x - drag.startX)),
            Math.max(1, Math.abs(y - drag.startY))
        ];

        render();
        return;
    }

    if (drag.kind === 'resize') {
        resizeRect(drag.shape, drag.index, worldX, worldY);
    } else if (drag.kind === 'node') {
        moveNode(drag.shape, drag.index, worldX, worldY);
    } else {
        const dx = Math.round(worldX - drag.originX);
        const dy = Math.round(worldY - drag.originY);

        if (dx === 0 && dy === 0) {
            return;
        }

        moveShape(drag.shape, dx, dy);
        drag.originX += dx;
        drag.originY += dy;
    }

    drag.moved = true;
    showProperties(drag.shape);
    render();
});

function updateHover(event, worldX, worldY) {
    if (event.target !== dom.canvas || state.tool) {
        return;
    }

    const hovered = pick(view, state.shapes, state.visible, worldX, worldY);

    if (hovered !== state.hovered) {
        state.hovered = hovered;
        render();
    }
}

window.addEventListener('mouseup', () => {
    const drag = state.drag;

    state.drag = null;
    dom.canvas.classList.remove('dragging');

    if (drag && drag.kind === 'draw-rect') {
        completeTool();
        return;
    }

    if (!drag || drag.kind === 'pan' || !drag.moved) {
        return;
    }

    state.undo.push({ shape: drag.shape, geometry: drag.before });
    snapshot(drag.shape, drag.before);
    markDirty(drag.shape);
});

dom.canvas.addEventListener('wheel', (event) => {
    event.preventDefault();
    view.zoomAt(event.offsetX, event.offsetY, event.deltaY < 0 ? 1.2 : 1 / 1.2);
    render();
}, { passive: false });

window.addEventListener('keydown', (event) => {
    // Never steal keys from the token box, a modal field or the filter.
    if (event.target.matches('input, select, textarea')) {
        if (event.key === 'Escape') {
            event.target.blur();
        }

        return;
    }

    if (event.ctrlKey || event.metaKey) {
        const pressed = event.key.toLowerCase();

        if (pressed === 's') { event.preventDefault(); save(); return; }
        if (pressed === 'd') { event.preventDefault(); duplicateSelected(); return; }
        if (pressed === 'z') { event.preventDefault(); undo(); return; }

        return;
    }

    if (event.code === 'Space') {
        event.preventDefault();
        state.spaceDown = true;
        return;
    }

    if (event.key === 'Escape') {
        hideMenu();
        state.tool ? cancelTool() : select(null);
        render();
        return;
    }

    if (event.key === 'Enter' && state.tool) {
        event.preventDefault();
        finishTool();
        return;
    }

    if (event.key === 'Delete' || event.key === 'Backspace') {
        event.preventDefault();
        deleteSelected();
        return;
    }

    if (event.key.toLowerCase() === 'f') {
        event.preventDefault();
        dom.filter.focus();
        dom.filter.select();
        return;
    }

    const nudge = {
        ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1]
    }[event.key];

    if (nudge) {
        event.preventDefault();
        nudgeSelected(nudge[0], nudge[1], event.shiftKey ? 10 : 1);
    }
});

window.addEventListener('keyup', (event) => {
    if (event.code === 'Space') {
        state.spaceDown = false;
    }
});

function nudgeSelected(dx, dy, step) {
    const shape = state.selected;

    if (!shape) {
        return;
    }

    const before = geometryOf(shape);

    moveShape(shape, dx * step, dy * step);

    state.undo.push({ shape, geometry: before });
    snapshot(shape, before);
    markDirty(shape);
    showProperties(shape);
    render();
}

function undo() {
    const step = state.undo.pop();

    if (!step) {
        return;
    }

    applyGeometry(step.shape, step.geometry);

    // The shape stays dirty: undoing back to the on-disk value still needs a save to be sure, and
    // guessing wrong in the other direction would silently drop an edit.
    showProperties(step.shape);
    render();
    setStatus('Undone.');
}

/** Records the server's value for a shape the first time it changes, for discard to restore. */
function snapshot(shape, geometry) {
    if (state.baseline.has(shape)) {
        return;
    }

    const props = {};

    for (const field of shape.fields || []) {
        props[field.path || field.key] = String(shape.props?.[field.key] ?? '');
    }

    state.baseline.set(shape, {
        geometry,
        props: Object.keys(props).length > 0 ? props : undefined
    });
}

function markDirty(shape) {
    state.dirty.add(shape);
    updateToolbar();
}

// --- selection and properties ------------------------------------------------------------------

function select(shape) {
    state.selected = shape;
    showProperties(shape);
    updateToolbar();
}

function showProperties(shape) {
    dom.properties.innerHTML = '';

    if (!shape) {
        dom.properties.innerHTML = '<p class="muted">Nothing selected.</p>';
        return;
    }

    const name = document.createElement('p');
    name.className = 'name';
    name.textContent = shape.label || shape.id;

    const kind = document.createElement('p');
    kind.className = 'kind';
    kind.textContent = `${shape.kind} · ${LAYERS[shape.layer].label}`;

    dom.properties.append(name, kind);

    // Geometry is edited on the canvas, so it is shown but not typed into.
    if (shape.kind === 'rect') {
        const [x, y, w, h] = shape.rect;
        dom.properties.append(readonlyPair('X', x, 'Y', y), readonlyPair('Width', w, 'Height', h));
    } else if (shape.kind === 'point') {
        const [x, y, z] = shape.points[0];
        dom.properties.append(readonlyPair('X', x, 'Y', y), readonlyField('Z', z));
    } else {
        dom.properties.append(readonlyField('Nodes', shape.points.length));
    }

    for (const field of shape.fields || []) {
        dom.properties.append(editableField(shape, field));
    }

    const where = document.createElement('p');
    where.className = 'kind';
    where.style.whiteSpace = 'pre-line';
    where.textContent = `${shape.file}\n${shape.pointer}`;
    dom.properties.append(where);
}

function editableField(shape, field) {
    const label = document.createElement('label');
    const caption = document.createElement('span');
    caption.textContent = field.label;

    const input = document.createElement('input');
    input.type = field.type === 'int' ? 'number' : 'text';
    input.value = shape.props?.[field.key] ?? '';
    input.dataset.field = field.key;
    input.autocomplete = 'off';

    const list = {
        map: 'facet-list', vendor: 'vendor-list', creature: 'creature-list', route: 'route-list'
    }[field.type];

    if (list) {
        input.setAttribute('list', list);
    }

    // Edits join the same dirty set as a drag, so one Save covers geometry and properties together
    // and Discard can put both back.
    input.addEventListener('change', () => {
        snapshot(shape, geometryOf(shape));

        shape.props = shape.props || {};
        shape.editedProps = shape.editedProps || {};
        shape.props[field.key] = input.value;
        shape.editedProps[field.key] = true;

        // Renaming should show immediately; the label is what the filter and the menus display.
        if (field.key === 'name') {
            shape.label = input.value;
        }

        markDirty(shape);
        applyFilter();
        render();
        setStatus('Edited - press Save to apply.', 'ok');
    });

    label.append(caption, input);

    return label;
}

function readonlyField(label, value) {
    const wrap = document.createElement('label');
    const caption = document.createElement('span');
    const input = document.createElement('input');

    caption.textContent = label;
    input.value = value;
    input.readOnly = true;

    wrap.append(caption, input);

    return wrap;
}

function readonlyPair(labelA, valueA, labelB, valueB) {
    const row = document.createElement('div');
    row.className = 'row';
    row.append(readonlyField(labelA, valueA), readonlyField(labelB, valueB));

    return row;
}

// --- status ----------------------------------------------------------------------------------

let statusTimer = null;

function setStatus(message, kind) {
    dom.status.textContent = message;
    dom.status.className = kind || '';

    clearTimeout(statusTimer);
    statusTimer = setTimeout(() => {
        dom.status.textContent = '';
        dom.status.className = '';
    }, 6000);
}
