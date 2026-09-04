// Wiring: state, input, undo, save.

import { api, loadStoredToken, setToken, clearToken } from './api.js';
import { View, DEFAULT_FACET } from './view.js';
import {
    LAYERS, draw, drawEntities, hitTest, geometryOf, applyGeometry, moveShape, resizeRect, moveNode
} from './shapes.js';

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
    properties: document.getElementById('properties'),
    coords: document.getElementById('coords'),
    status: document.getElementById('status')
};

const state = {
    facets: [],
    shapes: [],
    entities: [],
    visible: new Set(['zones', 'dailylife', 'spawners', 'entities']),
    selected: null,
    // Shapes edited since the last save, so a save sends only what changed.
    dirty: new Set(),
    // Session undo: each entry restores one shape's geometry to what it was before a drag.
    undo: [],
    drag: null
};

const view = new View(dom.canvas);
view.onTileLoaded = () => render();

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

        // Size the canvas before choosing a view: setFacet's scale clamp reads the canvas box, and
        // the workspace has only just become visible.
        view.resize();
        showFacet(state.facets.find((f) => f.name === DEFAULT_FACET) || state.facets[0]);

        await refreshShapes();
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

/** Keeps the picker and the view on the same facet - setting one without the other showed Felucca
 *  in the dropdown while the map was on Trammel. */
function showFacet(facet) {
    view.setFacet(facet);
    dom.facet.value = facet.name;
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

function updateCounts() {
    for (const element of dom.layers.querySelectorAll('.count')) {
        const layer = element.dataset.layer;

        element.textContent = layer === 'entities'
            ? state.entities.filter((e) => e.map === view.facet.name).length
            : state.shapes.filter((s) => s.layer === layer && s.map === view.facet.name).length;
    }
}

// --- data ------------------------------------------------------------------------------------

async function refreshShapes() {
    const response = await api.shapes();
    state.shapes = response.shapes;
    state.dirty.clear();
    state.undo.length = 0;
    dom.save.disabled = true;
    updateCounts();
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
        draw(view.ctx, view, state.shapes, state.visible, state.selected);

        if (state.visible.has('entities')) {
            drawEntities(view.ctx, view, state.entities);
        }
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

// --- input -----------------------------------------------------------------------------------

dom.canvas.addEventListener('mousedown', (event) => {
    if (event.button !== 0) {
        return;
    }

    const [worldX, worldY] = view.toWorld(event.offsetX, event.offsetY);
    const hit = hitTest(view, state.shapes, state.visible, state.selected, worldX, worldY);

    if (!hit) {
        select(null);
        state.drag = { kind: 'pan', lastX: event.clientX, lastY: event.clientY };
        dom.canvas.classList.add('dragging');
        render();
        return;
    }

    select(hit.shape);

    // Snapshot before the first movement, so one drag is one undo step.
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
});

window.addEventListener('mousemove', (event) => {
    if (view.facet) {
        const rect = dom.canvas.getBoundingClientRect();
        const [worldX, worldY] = view.toWorld(event.clientX - rect.left, event.clientY - rect.top);
        dom.coords.textContent = `${Math.floor(worldX)}, ${Math.floor(worldY)}`;
    }

    const drag = state.drag;

    if (!drag) {
        return;
    }

    if (drag.kind === 'pan') {
        view.panBy(event.clientX - drag.lastX, event.clientY - drag.lastY);
        drag.lastX = event.clientX;
        drag.lastY = event.clientY;
        render();
        return;
    }

    const rect = dom.canvas.getBoundingClientRect();
    const [worldX, worldY] = view.toWorld(event.clientX - rect.left, event.clientY - rect.top);

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

window.addEventListener('mouseup', () => {
    const drag = state.drag;

    state.drag = null;
    dom.canvas.classList.remove('dragging');

    if (!drag || drag.kind === 'pan' || !drag.moved) {
        return;
    }

    state.undo.push({ shape: drag.shape, geometry: drag.before });
    markDirty(drag.shape);
});

dom.canvas.addEventListener('wheel', (event) => {
    event.preventDefault();
    view.zoomAt(event.offsetX, event.offsetY, event.deltaY < 0 ? 1.2 : 1 / 1.2);
    render();
}, { passive: false });

window.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
        select(null);
        render();
        return;
    }

    if (event.key.toLowerCase() === 'z' && (event.ctrlKey || event.metaKey)) {
        event.preventDefault();
        undo();
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

// --- saving ------------------------------------------------------------------------------------

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
        dom.save.disabled = false;
        setStatus(`Save failed: ${error.message}`, 'error');
        return;
    }

    // Reload only the systems whose files actually changed, and report the first failure with the
    // server's own message - a config that will not validate is the common case here, and its
    // reason is the useful part.
    for (const layer of layers) {
        const system = LAYERS[layer].reload;

        if (!system) {
            continue;
        }

        try {
            await api.reload(system);
        } catch (error) {
            setStatus(`Saved, but ${system} reload failed: ${error.message}`, 'error');
            await refreshShapes();
            render();
            return;
        }
    }

    setStatus('Saved and reloaded.', 'ok');

    await refreshShapes();
    select(null);
    render();
}

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
