// Wiring: state, input, undo, save, create and delete.

import { api, loadStoredToken, setToken, clearToken } from './api.js';
import { View, DEFAULT_FACET } from './view.js';
import {
    LAYERS, draw, drawEntities, drawDraft, hitTest, pick, geometryOf, applyGeometry, moveShape,
    resizeRect, moveNode
} from './shapes.js';
import { TOOLS, askFor, loadTypeLists, fillRouteList } from './tools.js';

const ENTITY_POLL_MS = 1500;

const dom = {
    gate: document.getElementById('token-gate'),
    form: document.getElementById('token-form'),
    tokenInput: document.getElementById('token-input'),
    tokenError: document.getElementById('token-error'),
    workspace: document.getElementById('workspace'),
    canvas: document.getElementById('map'),
    facet: document.getElementById('facet'),
    save: document.getElementById('save'),
    layers: document.getElementById('layers'),
    createButtons: document.getElementById('create-buttons'),
    properties: document.getElementById('properties'),
    coords: document.getElementById('coords'),
    hint: document.getElementById('hint'),
    status: document.getElementById('status'),
    banner: document.getElementById('banner'),
    bannerText: document.getElementById('banner-text'),
    bannerDiscard: document.getElementById('banner-discard'),
    bannerDismiss: document.getElementById('banner-dismiss')
};

const state = {
    facets: [],
    shapes: [],
    entities: [],
    visible: new Set(['zones', 'dailylife', 'spawners', 'entities']),
    selected: null,
    hovered: null,
    // Shapes edited since the last save, so a save sends only what changed.
    dirty: new Set(),
    // Session undo: each entry restores one shape's geometry to what it was before a drag.
    undo: [],
    // The server's value for each shape at the moment it first became dirty. This is what makes
    // "discard" able to put the FILE back, not just the view - which matters because a save whose
    // reload was rejected has already written to disk.
    baseline: new Map(),
    drag: null,
    spaceDown: false,
    // The active create tool, and the geometry it has collected so far.
    tool: null,
    draft: null
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
        buildCreateButtons();

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
    render();
}

function buildLayerList() {
    dom.layers.innerHTML = '';

    for (const [key, layer] of Object.entries(LAYERS)) {
        const item = document.createElement('li');
        const label = document.createElement('label');
        const checkbox = document.createElement('input');

        checkbox.type = 'checkbox';
        checkbox.checked = state.visible.has(key);
        checkbox.addEventListener('change', () => {
            checkbox.checked ? state.visible.add(key) : state.visible.delete(key);

            if (state.selected && !checkbox.checked && state.selected.layer === key) {
                select(null);
            }

            render();
        });

        const swatch = document.createElement('span');
        swatch.className = 'swatch';
        swatch.style.background = layer.color;

        const count = document.createElement('span');
        count.className = 'count';
        count.dataset.layer = key;

        label.append(checkbox, swatch, document.createTextNode(layer.label), count);
        item.append(label);
        dom.layers.append(item);
    }
}

function buildCreateButtons() {
    dom.createButtons.innerHTML = '';

    for (const [key, tool] of Object.entries(TOOLS)) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = tool.label;
        button.dataset.tool = key;
        button.addEventListener('click', () => startTool(key));
        dom.createButtons.append(button);
    }
}

function updateCounts() {
    for (const element of dom.layers.querySelectorAll('.count')) {
        const layer = element.dataset.layer;

        element.textContent = layer === 'entities'
            ? state.entities.filter((e) => e.map === view.facet.name).length
            : state.shapes.filter((s) => s.layer === layer && s.map === view.facet.name).length;
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
    dom.save.disabled = true;

    fillRouteList(state.shapes);
    showProperties(null);
    updateCounts();

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
        draw(view.ctx, view, state.shapes, state.visible, state.selected, state.hovered);

        if (state.visible.has('entities')) {
            drawEntities(view.ctx, view, state.entities);
        }

        drawDraft(view.ctx, view, state.draft);
    });
}

// A ResizeObserver on the canvas itself, rather than a window resize listener: it also fires for
// the first real layout after the workspace stops being hidden, which is when the buffer must be
// sized. The media query covers a move to a display with a different pixel ratio, where the CSS
// box does not change but the buffer needs to.
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

// --- create tools ------------------------------------------------------------------------------

function startTool(key) {
    cancelTool();

    const tool = TOOLS[key];

    state.tool = { key, ...tool, phase: 0 };
    state.draft = { kind: tool.kind === 'rect' ? 'rect' : 'points', points: [], rect: null };

    for (const button of dom.createButtons.querySelectorAll('button')) {
        button.classList.toggle('active', button.dataset.tool === key);
    }

    select(null);

    // A townsperson has no map geometry - it starts wherever its route starts - so it goes straight
    // to the form.
    if (tool.kind === 'form') {
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

    for (const button of dom.createButtons.querySelectorAll('button')) {
        button.classList.remove('active');
    }

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

/** Enter, or a double click, ends a multi-node tool. */
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
    } else if (tool.kind === 'point') {
        request.points = draft.points;
    } else if (tool.kind === 'route') {
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

// --- delete ------------------------------------------------------------------------------------

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

dom.save.addEventListener('click', save);

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
            rect: shape.kind === 'rect' ? shape.rect : undefined,
            points: shape.kind === 'rect' ? undefined : shape.points
        });

        layers.add(shape.layer);
    }

    dom.save.disabled = true;
    setStatus('Saving...');

    try {
        await api.patch(edits);
    } catch (error) {
        // Nothing was written. Keep the edits so they can be corrected and saved again.
        dom.save.disabled = false;
        showBanner(`Save failed, nothing was written: ${error.message}`);
        return;
    }

    await reloadAndRefresh([...layers], 'Saved and reloaded.');
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
            dom.save.disabled = state.dirty.size === 0;

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
    render();
    setStatus(successMessage, 'ok');

    return true;
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

/**
 * Puts everything back the way the server has it - including the file.
 *
 * Just re-reading the shapes would not be enough. A save whose reload was rejected has already
 * written to disk, so the file and the running config disagree; dropping the local edits alone
 * would leave that bad file in place to fail on the next restart. Writing the baselines back is
 * what makes "discard" mean discard.
 */
dom.bannerDiscard.addEventListener('click', async () => {
    const rollback = [];
    const layers = new Set();

    for (const [shape, geometry] of state.baseline) {
        rollback.push({
            layer: shape.layer,
            file: shape.file,
            pointer: shape.pointer,
            rect: geometry.rect,
            points: geometry.points
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
});

// --- input -----------------------------------------------------------------------------------

dom.canvas.addEventListener('mousedown', (event) => {
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

    if (!state.baseline.has(drag.shape)) {
        state.baseline.set(drag.shape, drag.before);
    }

    markDirty(drag.shape);
});

dom.canvas.addEventListener('wheel', (event) => {
    event.preventDefault();
    view.zoomAt(event.offsetX, event.offsetY, event.deltaY < 0 ? 1.2 : 1 / 1.2);
    render();
}, { passive: false });

window.addEventListener('keydown', (event) => {
    // Never steal keys from the token box or a modal field.
    if (event.target.matches('input, select, textarea')) {
        return;
    }

    if (event.code === 'Space') {
        event.preventDefault();
        state.spaceDown = true;
        return;
    }

    if (event.key === 'Escape') {
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

    if (event.key.toLowerCase() === 'z' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        undo();
    }
});

window.addEventListener('keyup', (event) => {
    if (event.code === 'Space') {
        state.spaceDown = false;
    }
});

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

function markDirty(shape) {
    state.dirty.add(shape);
    dom.save.disabled = false;
}

// --- selection and properties ------------------------------------------------------------------

function select(shape) {
    state.selected = shape;
    showProperties(shape);
}

function showProperties(shape) {
    if (!shape) {
        dom.properties.innerHTML = '<p class="muted">Nothing selected.</p>';
        return;
    }

    const rows = [];

    if (shape.kind === 'rect') {
        const [x, y, w, h] = shape.rect;
        rows.push(pair('X', x, 'Y', y), pair('Width', w, 'Height', h));
    } else if (shape.kind === 'point') {
        const [x, y, z] = shape.points[0];
        rows.push(pair('X', x, 'Y', y), field('Z', z));
    } else {
        rows.push(field('Nodes', shape.points.length));
    }

    for (const [key, value] of Object.entries(shape.props || {})) {
        if (value !== null && typeof value === 'object') {
            rows.push(field(key, Array.isArray(value) ? value.join(', ') : JSON.stringify(value)));
        } else if (value !== null && value !== undefined) {
            rows.push(field(key, value));
        }
    }

    dom.properties.innerHTML = `
        <p class="name">${escapeHtml(shape.label || shape.id)}</p>
        <p class="kind">${shape.kind} &middot; ${escapeHtml(LAYERS[shape.layer].label)}</p>
        ${rows.join('')}
        <p class="kind">${escapeHtml(shape.file)}<br>${escapeHtml(shape.pointer)}</p>
    `;
}

function field(label, value) {
    return `<label><span>${escapeHtml(label)}</span>
        <input value="${escapeHtml(String(value))}" readonly></label>`;
}

function pair(labelA, valueA, labelB, valueB) {
    return `<div class="row">${field(labelA, valueA)}${field(labelB, valueB)}</div>`;
}

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
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
