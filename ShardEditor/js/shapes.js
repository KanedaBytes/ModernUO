// Drawing, hit-testing and editing the three shape kinds.
//
// Every layer arrives in the same vocabulary - rect, polyline, point - because the API projects
// the underlying config files into it. Nothing here knows that a tavern rectangle is stored with a
// Z and a shop district is not, or that one route is a dictionary entry and another is an inline
// array. That is the whole reason the projection exists.

export const LAYERS = {
    zones: { label: 'Restricted zones', color: '#ff7a6b', reload: 'zones' },
    dailylife: { label: 'Daily life', color: '#6fb3ff', reload: 'dailylife' },
    spawners: { label: 'Spawners', color: '#c792ea', reload: 'spawners' },
    entities: { label: 'Live entities', color: '#7bd88f', reload: null }
};

const HANDLE = 5;
const GRAB = 7;

export function draw(ctx, view, shapes, visible, selected) {
    for (const shape of shapes) {
        if (!visible.has(shape.layer) || shape.map !== view.facet.name) {
            continue;
        }

        drawShape(ctx, view, shape, shape === selected);
    }
}

function drawShape(ctx, view, shape, isSelected) {
    const color = LAYERS[shape.layer].color;

    ctx.lineWidth = isSelected ? 2 : 1;
    ctx.strokeStyle = isSelected ? '#ffffff' : color;
    ctx.fillStyle = color;

    if (shape.kind === 'rect') {
        const [x, y, w, h] = shape.rect;
        const [sx, sy] = view.toScreen(x, y);
        const sw = w * view.scale;
        const sh = h * view.scale;

        ctx.globalAlpha = 0.18;
        ctx.fillRect(sx, sy, sw, sh);
        ctx.globalAlpha = 1;
        ctx.strokeRect(sx, sy, sw, sh);

        if (isSelected) {
            for (const [hx, hy] of rectHandles(shape.rect)) {
                const [px, py] = view.toScreen(hx, hy);
                ctx.fillStyle = '#ffffff';
                ctx.fillRect(px - HANDLE / 2, py - HANDLE / 2, HANDLE, HANDLE);
            }
        }

        label(ctx, view, shape, x + w / 2, y);
        return;
    }

    if (shape.kind === 'polyline') {
        ctx.beginPath();

        shape.points.forEach(([x, y], i) => {
            // Route nodes are tile centres, not corners; the half-tile offset is what makes a line
            // drawn between them sit on the tiles the NPC actually walks.
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5);
            i === 0 ? ctx.moveTo(sx, sy) : ctx.lineTo(sx, sy);
        });

        if (shape.props && shape.props.closed) {
            ctx.closePath();
        }

        ctx.stroke();

        for (const [x, y] of shape.points) {
            const [sx, sy] = view.toScreen(x + 0.5, y + 0.5);
            ctx.fillStyle = isSelected ? '#ffffff' : color;
            ctx.fillRect(sx - HANDLE / 2, sy - HANDLE / 2, HANDLE, HANDLE);
        }

        // Only when selected. Ten routes and walk-home paths all labelled at once buries the town
        // centre in overlapping text, and a polyline is identifiable from its shape anyway - unlike
        // a point, where the label is the only thing telling two markers apart.
        if (isSelected) {
            label(ctx, view, shape, shape.points[0][0], shape.points[0][1]);
        }

        return;
    }

    const [x, y] = shape.points[0];
    const [sx, sy] = view.toScreen(x + 0.5, y + 0.5);

    ctx.beginPath();
    ctx.arc(sx, sy, isSelected ? 6 : 4, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();

    label(ctx, view, shape, x, y);
}

function label(ctx, view, shape, x, y) {
    // Only once the map is legible enough for the name to mean something.
    if (view.scale < 0.35 || !shape.label) {
        return;
    }

    const [sx, sy] = view.toScreen(x, y);

    ctx.font = '11px ui-sans-serif, system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.fillStyle = 'rgba(0, 0, 0, .65)';

    const width = ctx.measureText(shape.label).width;
    ctx.fillRect(sx - width / 2 - 3, sy - 17, width + 6, 14);

    ctx.fillStyle = '#ffffff';
    ctx.fillText(shape.label, sx, sy - 6);
    ctx.textAlign = 'left';
}

export function drawEntities(ctx, view, entities) {
    for (const entity of entities) {
        if (entity.map !== view.facet.name) {
            continue;
        }

        const [sx, sy] = view.toScreen(entity.x + 0.5, entity.y + 0.5);

        ctx.beginPath();
        ctx.arc(sx, sy, 4, 0, Math.PI * 2);
        ctx.fillStyle = entity.kind === 'npc' ? '#7bd88f' : entity.kind === 'staff' ? '#ffd479' : '#ffffff';
        ctx.fill();
        ctx.strokeStyle = 'rgba(0, 0, 0, .7)';
        ctx.lineWidth = 1;
        ctx.stroke();

        if (view.scale >= 0.5 && entity.name) {
            ctx.font = '10px ui-sans-serif, system-ui, sans-serif';
            ctx.fillStyle = 'rgba(255, 255, 255, .85)';
            ctx.fillText(entity.name, sx + 7, sy + 3);
        }
    }
}

function rectHandles([x, y, w, h]) {
    return [[x, y], [x + w, y], [x, y + h], [x + w, y + h]];
}

/**
 * What is under the cursor, nearest first. Handles beat bodies so a corner is always grabbable
 * even when it sits inside another shape.
 */
export function hitTest(view, shapes, visible, selected, worldX, worldY) {
    const slack = GRAB / view.scale;

    if (selected && visible.has(selected.layer) && selected.map === view.facet.name) {
        if (selected.kind === 'rect') {
            const handles = rectHandles(selected.rect);

            for (let i = 0; i < handles.length; i++) {
                if (near(handles[i], worldX, worldY, slack)) {
                    return { shape: selected, mode: 'resize', index: i };
                }
            }
        } else {
            for (let i = 0; i < selected.points.length; i++) {
                const [px, py] = selected.points[i];

                if (near([px + 0.5, py + 0.5], worldX, worldY, slack)) {
                    return { shape: selected, mode: 'node', index: i };
                }
            }
        }
    }

    // Reverse order so the shape drawn last - the one visually on top - is picked first.
    for (let i = shapes.length - 1; i >= 0; i--) {
        const shape = shapes[i];

        if (!visible.has(shape.layer) || shape.map !== view.facet.name) {
            continue;
        }

        if (shape.kind === 'rect') {
            const [x, y, w, h] = shape.rect;

            if (worldX >= x && worldX <= x + w && worldY >= y && worldY <= y + h) {
                return { shape, mode: 'move' };
            }
        } else {
            for (let n = 0; n < shape.points.length; n++) {
                const [px, py] = shape.points[n];

                if (near([px + 0.5, py + 0.5], worldX, worldY, slack)) {
                    return { shape, mode: shape.kind === 'point' ? 'move' : 'node', index: n };
                }
            }
        }
    }

    return null;
}

function near([x, y], worldX, worldY, slack) {
    return Math.abs(x - worldX) <= slack && Math.abs(y - worldY) <= slack;
}

export function geometryOf(shape) {
    return shape.kind === 'rect'
        ? { rect: shape.rect.slice() }
        : { points: shape.points.map((p) => p.slice()) };
}

export function applyGeometry(shape, geometry) {
    if (geometry.rect) {
        shape.rect = geometry.rect.slice();
    } else {
        shape.points = geometry.points.map((p) => p.slice());
    }
}

export function moveShape(shape, dx, dy) {
    if (shape.kind === 'rect') {
        shape.rect[0] += dx;
        shape.rect[1] += dy;
        return;
    }

    for (const point of shape.points) {
        point[0] += dx;
        point[1] += dy;
    }
}

/**
 * Drags one corner. The opposite corner is the anchor, so dragging past it flips the rectangle
 * rather than inverting its size - a negative width would be rejected by the server and is not a
 * state the editor should be able to reach.
 */
export function resizeRect(shape, index, worldX, worldY) {
    const [x, y, w, h] = shape.rect;
    const anchorX = index === 0 || index === 2 ? x + w : x;
    const anchorY = index === 0 || index === 1 ? y + h : y;

    const left = Math.min(anchorX, worldX);
    const top = Math.min(anchorY, worldY);

    shape.rect = [
        Math.round(left),
        Math.round(top),
        Math.max(1, Math.round(Math.abs(anchorX - worldX))),
        Math.max(1, Math.round(Math.abs(anchorY - worldY)))
    ];
}

export function moveNode(shape, index, worldX, worldY) {
    shape.points[index][0] = Math.round(worldX - 0.5);
    shape.points[index][1] = Math.round(worldY - 0.5);
}
