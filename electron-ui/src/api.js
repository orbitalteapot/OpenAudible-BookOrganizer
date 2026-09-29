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
 * Whether `error` is a request that got no answer at all: the organizer could not be reached, or did
 * not answer in time. Such an error says nothing about the settings or the folders, and nothing once
 * the organizer answers again; any other is its answer, true until something changes it.
 */
export function isUnanswered(error) {
  return error?.code === 'unreachable' || error?.code === 'timeout';
}

/**
 * Answers a proxy gives for a server behind it that is not answering: nginx, Traefik and the NAS
 * proxies in front of a container send these while it restarts.
 */
const GATEWAY_STATUSES = new Set([502, 503, 504]);

/**
 * The error a failed response describes. The backend answers with JSON, but a crashed or
 * not-yet-started backend answers with HTML or nothing at all, and letting res.json() throw there
 * would replace a useful message with a JSON parse error.
 *
 * An answer that is not the backend's own (a proxy's error page, a gateway status) means the
 * organizer is not answering, so it is the same error as a failed connection: the page then says
 * so in plain words, and clears it once the organizer answers again, instead of showing
 * "HTTP 502" until some later save happens to work.
 */
async function readError(res) {
  let body = null;

  try {
    body = JSON.parse(await res.text());
  } catch {
    // Not JSON, so not the backend's answer.
  }

  if (body === null || GATEWAY_STATUSES.has(res.status)) {
    return new ApiError(unreachableMessage(), { status: res.status, code: 'unreachable' });
  }

  return new ApiError(body.error || `Request failed (HTTP ${res.status})`, {
    status: res.status,
    code: body.code ?? null,
    field: body.field ?? null,
  });
}

/**
 * How long any request may take before it is given up on. Reading the settings looks at every
 * folder, and on an offline network share (a hard NFS mount) that can hang for good; so can saving
 * them, starting a sort and reading the export, which look at the same folders. The settings hook
 * sends its requests one at a time, so one that never ends would hold up every save and refresh
 * behind it, with the controls waiting on it and nothing said.
 */
const REQUEST_TIMEOUT_MS = 60_000;

/**
 * The run's progress only reads the organizer's memory, so it never needs long. Polls that hang for
 * a minute each (a paused container, a dropped VPN) would take half an hour to add up to "lost contact".
 */
const PROGRESS_TIMEOUT_MS = 5_000;

async function request(path, { method = 'GET', body, timeoutMs = REQUEST_TIMEOUT_MS } = {}) {
  const options = { method, signal: AbortSignal.timeout(timeoutMs) };
  // Every change is sent as JSON, with or without a body: the desktop backend refuses anything else,
  // because a web site can only send JSON to it after a preflight the backend turns down.
  if (method !== 'GET') options.headers = { 'Content-Type': 'application/json' };
  if (body !== undefined) options.body = JSON.stringify(body);

  let res;
  try {
    res = await fetch(`${apiBase()}${path}`, options);
  } catch (err) {
    // Not the same as not answering at all: the organizer is there, but stuck on a folder. A change
    // may have been made all the same, which is for the caller to find out.
    if (err?.name === 'TimeoutError') {
      throw new ApiError(
        'The organizer did not answer in time. A network drive or disk that has stopped responding can cause this: check that it is connected.',
        { code: 'timeout' }
      );
    }
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
  return request('/api/sort/progress', { timeoutMs: PROGRESS_TIMEOUT_MS });
}

export function cancelSort() {
  return request('/api/sort/cancel', { method: 'POST' });
}
