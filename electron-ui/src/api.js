import { unreachableMessage } from './mode';

/** The dev backend's fixed address, for a plain browser tab on the Vite dev server. */
const DEV_BACKEND_URL = 'http://127.0.0.1:5123';
const VITE_DEV_PORT = '5173';

/**
 * Where the backend is. The desktop app's backend listens on a port Electron picked, which the
 * preload script hands over; the Vite dev server has no backend of its own, so a browser tab on it
 * talks to one started by hand; anything else was served by the backend itself.
 *
 * Read on every request rather than once at import, so it never depends on module load order.
 */
function apiBase() {
  if (typeof window === 'undefined') return '';
  if (window.electronAPI?.backendUrl) return window.electronAPI.backendUrl;
  if (window.location.port === VITE_DEV_PORT) return DEV_BACKEND_URL;
  return '';
}

/**
 * A request the backend refused or could not answer. `code` and `field` come from the backend's
 * error body when it sends them: `code` says what went wrong ("destinationMissing",
 * "alreadyRunning"), `field` which setting it is about ("destinationPath"), so the page can show
 * the message next to that setting.
 */
export class ApiError extends Error {
  constructor(message, { status = 0, code = null, field = null } = {}) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.field = field;
  }
}

/**
 * The error a failed response describes. The backend answers with JSON, but a crashed or
 * not-yet-started backend answers with HTML or nothing at all, and letting res.json() throw there
 * would replace a useful message with a JSON parse error.
 */
async function readError(res) {
  const fallback = `Request failed (HTTP ${res.status})`;
  let body = null;

  try {
    body = JSON.parse(await res.text());
  } catch {
    // Not JSON: the fallback message is the best there is.
  }

  return new ApiError(body?.error || fallback, {
    status: res.status,
    code: body?.code ?? null,
    field: body?.field ?? null,
  });
}

async function request(path, { method = 'GET', body } = {}) {
  const options = { method };
  // Every change is sent as JSON, with or without a body: the desktop backend refuses anything else,
  // because a web site can only send JSON to it after a preflight the backend turns down.
  if (method !== 'GET') options.headers = { 'Content-Type': 'application/json' };
  if (body !== undefined) options.body = JSON.stringify(body);

  let res;
  try {
    res = await fetch(`${apiBase()}${path}`, options);
  } catch {
    throw new ApiError(unreachableMessage(), { code: 'unreachable' });
  }

  if (!res.ok) throw await readError(res);
  return res.json();
}

export async function healthCheck() {
  try {
    await request('/api/health');
    return true;
  } catch {
    return false;
  }
}

/** The settings in force, with which of them the server fixes and whether each path can be found. */
export function getSettings() {
  return request('/api/settings');
}

/** Saves the fields in `patch` (wire names, e.g. `{ destinationPath }`) and returns the new settings. */
export function updateSettings(patch) {
  return request('/api/settings', { method: 'PUT', body: patch });
}

export function getSchedule() {
  return request('/api/schedule');
}

/** Reads the library export named in the settings: `{ books, skippedRows, warnings }`. */
export function parseLibrary() {
  return request('/api/books/parse', { method: 'POST' });
}

/** The books the backend read last, without reading the export again. */
export function getBooks() {
  return request('/api/books');
}

/**
 * Starts a sort with the saved settings. `comparisonMode` overrides the saved update check for this
 * run only; `createDestination` is sent once the user has agreed to create a missing destination.
 */
export function startSort({ comparisonMode, createDestination = false } = {}) {
  return request('/api/sort/start', { method: 'POST', body: { comparisonMode, createDestination } });
}

/** The current or most recent sort, however it was started. */
export function getRunStatus() {
  return request('/api/sort/progress');
}

export function cancelSort() {
  return request('/api/sort/cancel', { method: 'POST' });
}
