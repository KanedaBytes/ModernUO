// Talking to the admin API.
//
// The token goes in the Authorization header and nowhere else - never a cookie, never a query
// string. Both of those would be attached by the browser to a request a hostile page made on the
// user's behalf; a header the page has to set itself cannot be forged cross-origin.

const TOKEN_KEY = 'shard-editor-token';

let token = null;

export function loadStoredToken() {
    try {
        return localStorage.getItem(TOKEN_KEY);
    } catch {
        // Private windows and blocked site data throw rather than returning null.
        return null;
    }
}

export function setToken(value, remember) {
    token = value;

    if (!remember) {
        return;
    }

    try {
        localStorage.setItem(TOKEN_KEY, value);
    } catch {
        // Not being able to remember the token is not worth failing the session over.
    }
}

export function clearToken() {
    token = null;

    try {
        localStorage.removeItem(TOKEN_KEY);
    } catch {
        // Nothing to do.
    }
}

async function request(method, path, body) {
    const response = await fetch(path, {
        method,
        headers: {
            Authorization: `Bearer ${token}`,
            ...(body === undefined ? {} : { 'Content-Type': 'application/json' })
        },
        body: body === undefined ? undefined : JSON.stringify(body)
    });

    let payload = null;

    try {
        payload = await response.json();
    } catch {
        // A 500 with an empty body is still a failure worth reporting sensibly.
    }

    if (!response.ok) {
        const detail = payload && payload.error ? payload.error : `HTTP ${response.status}`;
        const error = new Error(detail);
        error.status = response.status;
        throw error;
    }

    return payload;
}

export const api = {
    status: () => request('GET', '/api/status'),
    shapes: () => request('GET', '/api/shapes'),
    entities: () => request('GET', '/api/entities'),
    staff: () => request('GET', '/api/staff'),
    patch: (edits) => request('PATCH', '/api/shapes', edits),
    // The empty object is required, not cosmetic: on Windows, http.sys rejects a POST with no
    // Content-Length with a 411 before the listener ever sees the request.
    reload: (system) => request('POST', `/api/reload/${system}`, {}),
    types: () => request('GET', '/api/types'),
    create: (shape) => request('POST', '/api/shapes/create', shape),
    // POST rather than DELETE: http.sys is particular about bodies on DELETE, and the pointer
    // identifying the shape has to travel somewhere.
    remove: (target) => request('POST', '/api/shapes/delete', target)
};
