const API_BASE = 'http://localhost:5123';

export async function healthCheck() {
  const res = await fetch(`${API_BASE}/api/health`);
  return res.ok;
}

export async function parseBooks(csvPath) {
  const res = await fetch(`${API_BASE}/api/books/parse`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ csvPath }),
  });
  if (!res.ok) {
    const err = await res.json();
    throw new Error(err.error || 'Failed to parse books');
  }
  return res.json();
}

export async function getBooks() {
  const res = await fetch(`${API_BASE}/api/books`);
  return res.json();
}

export async function startSort(csvPath, sourcePath, destinationPath) {
  const res = await fetch(`${API_BASE}/api/sort/start`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ csvPath, sourcePath, destinationPath }),
  });
  if (!res.ok) {
    const err = await res.json();
    throw new Error(err.error || 'Failed to start sort');
  }
  return res.json();
}

export async function getSortProgress() {
  const res = await fetch(`${API_BASE}/api/sort/progress`);
  return res.json();
}

export async function cancelSort() {
  const res = await fetch(`${API_BASE}/api/sort/cancel`, {
    method: 'POST',
  });
  if (!res.ok) {
    const err = await res.json();
    throw new Error(err.error || 'Failed to cancel sort');
  }
  return res.json();
}
