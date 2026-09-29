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
// The Docker image's settings, which the backend reads from its environment (ServerConfig). They are
// generic names a desktop user may have set for something else, or left exported after trying the
// Docker instructions, and any one of the paths would lock every folder on the Sort page to a
// "server setting" they never made. To try them on the desktop, run the backend by hand and point
// a development build at it with OABO_BACKEND_URL.
const CONTAINER_ONLY_VARIABLES = new Set([
  'CSV_PATH',
  'SOURCE_PATH',
  'DESTINATION_PATH',
  'SORT_INTERVAL',
  'COMPARISON_MODE',
  'OABO_MAX_PARALLELISM',
]);

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
    failure = `Part of the app is missing from the installation. Reinstall the app. (Missing: ${exe})`;
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
    failure = `Part of the app could not be started. Restart the app; if this keeps happening, reinstall it. (${err.message})`;
  });

  // Without this, a backend that dies mid-session leaves the UI waiting forever on requests
  // that will never be answered.
  proc.on('exit', (code, signal) => {
    console.error(`[API] Backend exited (code ${code}, signal ${signal})`);
    failure ??=
      code === SETTINGS_IN_USE_EXIT_CODE
        ? 'Another copy of the Book Organizer is still running. Wait a moment, then open the app again.'
        : `It closed unexpectedly. Restart the app to continue. (Error code: ${code === null ? signal : code})`;
    child = null;
    if (!stopping) onUnexpectedExit(failure);
  });
}

/** This process's environment, without the Docker image's settings (see CONTAINER_ONLY_VARIABLES). */
function desktopEnvironment() {
  // Upper-cased, because Windows matches variable names in any case and so does the backend there.
  return Object.fromEntries(
    Object.entries(process.env).filter(([name]) => !CONTAINER_ONLY_VARIABLES.has(name.toUpperCase()))
  );
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
    failure = `No free network port was available on this computer. Restart the app. (${err.message})`;
    return null;
  }

  const command = backendCommand(packaged);
  if (!command) return baseUrl;

  console.log(`[API] Starting backend on ${baseUrl}: ${command.file} ${command.args.join(' ')}`);
  child = spawn(command.file, command.args, {
    stdio: 'pipe',
    // The backend is a console program; started from this windowed app, Windows would give it
    // its own visible console window, and closing that window would stop the backend mid-sort.
    windowsHide: true,
    // OABO_PARENT_PID: the backend stops itself when this process is gone, so a crash or a
    // force-quit never leaves it running automatic sorts on its own.
    env: {
      ...desktopEnvironment(),
      ASPNETCORE_URLS: baseUrl,
      OABO_SETTINGS_PATH: settingsPath,
      OABO_PARENT_PID: String(process.pid),
    },
  });
  attachProcessLogging(child, onUnexpectedExit);
  return baseUrl;
}

/** The backend's JSON answer, or null when it did not answer or refused. `body` is sent as JSON. */
async function request(pathname, method = 'GET', body = undefined) {
  if (!baseUrl) return null;
  try {
    // The backend only accepts changes sent as JSON (see LocalRequestGuard).
    const headers = method === 'GET' ? {} : { 'Content-Type': 'application/json' };
    const res = await fetch(`${baseUrl}${pathname}`, {
      method,
      headers,
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
    });
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

/**
 * Cancels the running sort because the app is quitting, and waits up to `timeoutMs` for it to wind
 * down. Said to be the app closing, not a person cancelling, so an automatic sort cut short this way
 * is tried again at the next launch instead of counting as done for a whole interval.
 */
async function cancelSortAndWait(timeoutMs) {
  await request('/api/sort/cancel', 'POST', { reason: 'appClosing' });

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
      // windowsHide: taskkill is a console program too, and would flash a window on every quit.
      spawn('taskkill', ['/pid', String(proc.pid), '/f', '/t'], { windowsHide: true });
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
