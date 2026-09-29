/**
 * The .NET backend: starting it on a private port, talking to it, and stopping it.
 */
const { spawn } = require('child_process');
const fs = require('fs');
const net = require('net');
const path = require('path');

const START_TIMEOUT_MS = 60_000;
const POLL_INTERVAL_MS = 500;
// Short, because these requests sit between the user and a window closing or the app quitting.
const REQUEST_TIMEOUT_MS = 3_000;
// What the backend exits with when another copy of it still holds the settings file (SettingsFileLock).
const SETTINGS_IN_USE_EXIT_CODE = 75;

let baseUrl = null;
let child = null;
let failure = null; // plain-language reason once the backend has failed to start or has stopped
let stopping = false;

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

/**
 * Asks the OS for a port nobody is using. A fixed port would collide with a second copy of the
 * backend, such as the Docker image, and the app would silently drive that one instead.
 */
function findFreePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

function packagedExecutable() {
  const backendDir = path.join(process.resourcesPath, 'backend');
  return path.join(backendDir, process.platform === 'win32' ? 'ManagerApi.exe' : 'ManagerApi');
}

/** How to launch the backend, or null (with `failure` set) when it cannot be. */
function backendCommand(packaged) {
  if (!packaged) {
    // --no-launch-profile: a launchSettings.json would otherwise replace ASPNETCORE_URLS with its own port.
    return {
      file: 'dotnet',
      args: ['run', '--no-launch-profile', '--project', path.join(__dirname, '../../ManagerApi/ManagerApi.csproj')],
    };
  }

  const exe = packagedExecutable();
  if (!fs.existsSync(exe)) {
    failure = `The backend program is missing from the installation (${exe}). Reinstall the app.`;
    return null;
  }

  if (process.platform !== 'win32') {
    try {
      fs.chmodSync(exe, 0o755);
    } catch {
      // Best effort; the spawn reports the real problem if it is not executable.
    }
  }
  return { file: exe, args: [] };
}

function attachProcessLogging(proc, onUnexpectedExit) {
  proc.stdout?.on('data', (data) => console.log(`[API] ${data.toString().trim()}`));
  proc.stderr?.on('data', (data) => console.error(`[API] ${data.toString().trim()}`));

  proc.on('error', (err) => {
    console.error('[API] Failed to start backend:', err.message);
    failure = `The backend could not be started: ${err.message}`;
  });

  // Without this, a backend that dies mid-session leaves the UI waiting forever on requests
  // that will never be answered.
  proc.on('exit', (code, signal) => {
    console.error(`[API] Backend exited (code ${code}, signal ${signal})`);
    failure ??=
      code === SETTINGS_IN_USE_EXIT_CODE
        ? 'Another copy of the Book Organizer is still running. Wait a moment, then open the app again.'
        : `The backend stopped (exit code ${code === null ? signal : code}).`;
    child = null;
    if (!stopping) onUnexpectedExit(failure);
  });
}

/**
 * Starts the backend on 127.0.0.1 and returns its URL. It keeps its settings in `settingsPath`.
 * In a development build, OABO_BACKEND_URL points the app at a backend the developer runs
 * themselves (say, under a debugger) instead of starting one.
 */
async function start({ packaged, settingsPath, onUnexpectedExit }) {
  if (!packaged && process.env.OABO_BACKEND_URL) {
    baseUrl = process.env.OABO_BACKEND_URL.replace(/\/+$/, '');
    console.log(`[API] Using the backend at ${baseUrl} (OABO_BACKEND_URL)`);
    return baseUrl;
  }

  try {
    baseUrl = `http://127.0.0.1:${await findFreePort()}`;
  } catch (err) {
    failure = `No free network port for the backend: ${err.message}`;
    return null;
  }

  const command = backendCommand(packaged);
  if (!command) return baseUrl;

  console.log(`[API] Starting backend on ${baseUrl}: ${command.file} ${command.args.join(' ')}`);
  child = spawn(command.file, command.args, {
    stdio: 'pipe',
    // OABO_PARENT_PID: the backend stops itself when this process is gone, so a crash or a
    // force-quit never leaves it running automatic sorts on its own.
    env: {
      ...process.env,
      ASPNETCORE_URLS: baseUrl,
      OABO_SETTINGS_PATH: settingsPath,
      OABO_PARENT_PID: String(process.pid),
    },
  });
  attachProcessLogging(child, onUnexpectedExit);
  return baseUrl;
}

/** The backend's JSON answer, or null when it did not answer or refused. */
async function request(pathname, method = 'GET') {
  if (!baseUrl) return null;
  try {
    // The backend only accepts changes sent as JSON (see LocalRequestGuard).
    const headers = method === 'GET' ? {} : { 'Content-Type': 'application/json' };
    const res = await fetch(`${baseUrl}${pathname}`, { method, headers, signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) });
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

/** Resolves true once the backend answers, false if it died or took too long. */
async function waitUntilReady() {
  const deadline = Date.now() + START_TIMEOUT_MS;

  while (Date.now() < deadline) {
    if (await request('/api/health')) return true;

    // No point waiting out the full timeout for a process that has already died.
    if (failure) return false;

    await delay(POLL_INTERVAL_MS);
  }
  return false;
}

async function isSortRunning() {
  const status = await request('/api/sort/progress');
  return status?.state === 'running';
}

/** Cancels the running sort and waits up to `timeoutMs` for it to wind down. */
async function cancelSortAndWait(timeoutMs) {
  await request('/api/sort/cancel', 'POST');

  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline && (await isSortRunning())) {
    await delay(200);
  }
}

/** Stops the backend. On macOS and Linux it gets SIGTERM, which it handles by cancelling its run. */
function stop() {
  stopping = true;
  if (!child) return;

  const proc = child;
  child = null;

  try {
    if (process.platform === 'win32') {
      spawn('taskkill', ['/pid', String(proc.pid), '/f', '/t']);
    } else {
      proc.kill('SIGTERM');
    }
  } catch {
    // Best effort.
  }
}

module.exports = {
  start,
  waitUntilReady,
  request,
  isSortRunning,
  cancelSortAndWait,
  stop,
  url: () => baseUrl,
  failure: () => failure,
};
