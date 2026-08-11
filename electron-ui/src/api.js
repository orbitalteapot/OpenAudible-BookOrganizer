const isBrowser = typeof window !== 'undefined';
const isElectron = isBrowser && !!window.electronAPI;
const isViteDev = isBrowser && window.location.port === '5173';
const API_BASE = isElectron || isViteDev ? 'http://localhost:5123' : '';

/**
 * Pulls the error message out of a failed response. The backend answers with JSON, but a crashed
 * or not-yet-started backend answers with HTML or nothing at all, and letting res.json() throw
 * there replaces a useful message with a JSON parse error.
 */
async function readError(res) {
  try {
    const text = await res.text();
    if (!text) return `Request failed (HTTP ${res.status})`;

    try {
      const parsed = JSON.parse(text);
      return parsed?.error || `Request failed (HTTP ${res.status})`;
    } catch {
      return `Request failed (HTTP ${res.status})`;
    }
  } catch {
    return `Request failed (HTTP ${res.status})`;
  }
}

async function requestJson(path, options, fallbackMessage) {
  let res;
  try {
    res = await fetch(`${API_BASE}${path}`, options);
  } catch {
    throw new Error('Could not reach the backend. Check that it is still running.');
  }

  if (!res.ok) {
    throw new Error((await readError(res)) || fallbackMessage);
  }

  return res.json();
}

export async function healthCheck() {
  try {
    const res = await fetch(`${API_BASE}/api/health`);
    return res.ok;
  } catch {
    return false;
  }
}

export async function parseBooks(csvPath) {
  const data = await requestJson(
    '/api/books/parse',
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ csvPath }),
    },
    'Failed to parse books'
  );

  // Older backends replied with a bare array of books.
  if (Array.isArray(data)) {
    return { books: data, skippedRows: 0, warnings: [] };
  }

  return {
    books: data?.books ?? [],
    skippedRows: data?.skippedRows ?? 0,
    warnings: data?.warnings ?? [],
  };
}

export async function getBooks() {
  const data = await requestJson('/api/books', undefined, 'Failed to load books');
  return Array.isArray(data) ? data : [];
}

export function startSort(csvPath, sourcePath, destinationPath) {
  return requestJson(
    '/api/sort/start',
    {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ csvPath, sourcePath, destinationPath }),
    },
    'Failed to start sort'
  );
}

export function getSortProgress() {
  return requestJson('/api/sort/progress', undefined, 'Failed to read progress');
}

export function cancelSort() {
  return requestJson('/api/sort/cancel', { method: 'POST' }, 'Failed to cancel sort');
}

export function getAppConfig() {
  return requestJson('/api/config', undefined, 'Failed to load app configuration');
}
