// Exposes the server of the running DotNetLab program on a Portal relay (https://github.com/gosuda/portal-tunnel).
// The Go connector (eng/portal-bridge) calls dotnetlabPortalHandle for each visit,
// which is forwarded to .NET (src/Shared/WebServer.cs) and answered through respond().
// WebSockets go the same way: dotnetlabPortalSocket opens one (answered by socketOpened()),
// then messages flow through dotnetlabPortalSocketSend and socketMessage().

const REGISTRY_URL = 'https://raw.githubusercontent.com/gosuda/portal-tunnel/main/registry.json';
const PROBE_TIMEOUT_MS = 5000;
const READY_TIMEOUT_MS = 30000;

/** @type {((json: string) => void) | null} */
let onRequest = null;
/** @type {Promise<void> | null} */
let connector = null;
/** @type {Map<string, Promise<string>>} public URL per relay */
const exposures = new Map();
const pending = new Map();
/** @type {Map<number, { onMessage: (text: boolean, data: Uint8Array) => void, onClose: (code: number, reason: string) => void }>} */
const sockets = new Map();
let nextId = 0;
// Keeps the public address the same for the lifetime of this tab.
const name = crypto.randomUUID();

globalThis.dotnetlabPortalHandle = (request) => new Promise((resolve) => {
    if (!onRequest) {
        resolve(text(503, 'No server is running.'));
        return;
    }
    const id = nextId++;
    pending.set(id, { resolve });
    onRequest(JSON.stringify({
        id,
        kind: 'http',
        method: request.method,
        url: request.url,
        headers: flattenHeaders(request),
        body: toBase64(request.body),
    }));
});

function flattenHeaders(request) {
    const headers = ['Host', request.host];
    for (let i = 0; i < request.headers.length; i++) {
        headers.push(request.headers[i][0], request.headers[i][1]);
    }
    return headers;
}

globalThis.dotnetlabPortalSocket = (request, onMessage, onClose) => new Promise((resolve, reject) => {
    if (!onRequest) {
        reject(new Error('No server is running.'));
        return;
    }
    const id = nextId++;
    pending.set(id, { resolve, reject });
    sockets.set(id, { onMessage, onClose });
    onRequest(JSON.stringify({
        id,
        kind: 'websocket',
        method: request.method,
        url: request.url,
        headers: flattenHeaders(request),
        protocols: [...request.protocols],
        body: '',
    }));
});

globalThis.dotnetlabPortalSocketSend = (id, text, data) => {
    if (sockets.has(id)) {
        onRequest?.(JSON.stringify({ id, kind: 'send', text, body: toBase64(data) }));
    }
};

globalThis.dotnetlabPortalSocketClose = (id, code, reason) => {
    if (sockets.delete(id)) {
        onRequest?.(JSON.stringify({ id, kind: 'close', code, reason }));
    }
};

/**
 * @param {number} id
 * @param {string} protocol empty if none
 */
export function socketOpened(id, protocol) {
    const opening = pending.get(id);
    pending.delete(id);
    opening?.resolve({ id, protocol: protocol || null });
}

/**
 * @param {number} id
 * @param {string} message
 */
export function socketFailed(id, message) {
    const opening = pending.get(id);
    pending.delete(id);
    sockets.delete(id);
    opening?.reject(new Error(message));
}

/**
 * @param {number} id
 * @param {boolean} text
 * @param {Uint8Array} data
 */
export function socketMessage(id, text, data) {
    sockets.get(id)?.onMessage(text, new Uint8Array(data));
}

/**
 * @param {number} id
 * @param {number} code
 * @param {string} reason
 */
export function socketClosed(id, code, reason) {
    const socket = sockets.get(id);
    sockets.delete(id);
    socket?.onClose(code, reason);
}

/**
 * @param {number} id
 * @param {number} status
 * @param {string} headersJson flattened name/value pairs
 * @param {Uint8Array} body
 */
export function respond(id, status, headersJson, body) {
    const resolve = pending.get(id)?.resolve;
    pending.delete(id);
    const flat = JSON.parse(headersJson);
    const headers = [];
    for (let i = 0; i + 1 < flat.length; i += 2) {
        headers.push([flat[i], flat[i + 1]]);
    }
    resolve?.({ status, headers, body: new Uint8Array(body) });
}

/**
 * @param {string} relay a relay URL or empty to pick one from the Portal registry
 * @param {(json: string) => void} handler
 * @returns {Promise<string>} the public URL
 */
export async function expose(relay, handler) {
    onRequest = handler;
    const key = relay || '';
    let exposure = exposures.get(key);
    if (!exposure) {
        exposure = exposeOnFirst(relay ? [relay] : await listRelays());
        exposures.set(key, exposure);
        // Allow retrying after a failure.
        exposure.catch(() => exposures.delete(key));
    }
    return await exposure;
}

async function exposeOnFirst(relays) {
    await (connector ??= startConnector());
    const failures = [];
    for (const raw of relays) {
        const relay = raw.replace(/\/+$/, '');
        if (!(await supportsBrowsers(relay))) {
            failures.push(`${relay}: no browser connector support`);
            continue;
        }
        try {
            return await exposeOn(relay);
        } catch (error) {
            failures.push(`${relay}: ${error.message ?? error}`);
        }
    }
    throw new Error(`No relay could expose the server. ${failures.join('; ')}`);
}

async function startConnector() {
    await import('./wasm_exec.js');
    const go = new Go();
    const { instance } = await WebAssembly.instantiateStreaming(fetch(new URL('portal-bridge.wasm', import.meta.url)), go.importObject);
    go.run(instance);
}

function exposeOn(relay) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error('timed out')), READY_TIMEOUT_MS);
        globalThis.portalBridge(relay, name,
            (url) => { clearTimeout(timer); resolve(url); },
            (message) => { clearTimeout(timer); reject(new Error(message)); });
    });
}

async function listRelays() {
    try {
        return (await (await fetchWithin(REGISTRY_URL)).json()).relays ?? [];
    } catch {
        return [];
    }
}

// A relay that can host a browser serves its public certificate chain, which the
// connector needs because a browser cannot read it off a TLS handshake.
async function supportsBrowsers(relay) {
    try {
        const chain = await fetchWithin(`${relay}/sdk/certificate-chain`);
        return chain.ok && (await chain.text()).startsWith('-----BEGIN CERTIFICATE-----');
    } catch {
        return false;
    }
}

async function fetchWithin(url) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
    try {
        return await fetch(url, { signal: controller.signal });
    } finally {
        clearTimeout(timer);
    }
}

function text(status, message) {
    return { status, headers: [['Content-Type', 'text/plain; charset=utf-8']], body: new TextEncoder().encode(message) };
}

/** @param {Uint8Array} bytes */
function toBase64(bytes) {
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
        binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
}
