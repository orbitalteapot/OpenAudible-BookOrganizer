const { app, BrowserWindow, ipcMain, dialog } = require('electron');
const path = require('path');
const fs = require('fs');
const { spawn } = require('child_process');

const BACKEND_URL = 'http://localhost:5123';
const BACKEND_START_TIMEOUT_MS = 60_000;
const BACKEND_POLL_INTERVAL_MS = 500;

let mainWindow = null;
let backendProcess = null;
let backendExit = null; // { code, signal } once the backend has stopped
let quitting = false;

function isDev() {
  return !app.isPackaged;
}

function getBackendExecutable() {
  const backendDir = path.join(process.resourcesPath, 'backend');
  return process.platform === 'win32'
    ? path.join(backendDir, 'ManagerApi.exe')
    : path.join(backendDir, 'ManagerApi');
}

function attachProcessLogging(child) {
  child.stdout?.on('data', (data) => console.log(`[API] ${data.toString().trim()}`));
  child.stderr?.on('data', (data) => console.error(`[API] ${data.toString().trim()}`));

  child.on('error', (err) => {
    console.error('[API] Failed to start backend:', err.message);
    backendExit = { code: null, signal: null, error: err.message };
  });

  // Without this, a backend that dies mid-session leaves the UI waiting forever on requests
  // that will never be answered.
  child.on('exit', (code, signal) => {
    console.error(`[API] Backend exited (code ${code}, signal ${signal})`);
    backendExit = { code, signal };
    backendProcess = null;

    if (!quitting && mainWindow && !mainWindow.isDestroyed()) {
      dialog.showMessageBox(mainWindow, {
        type: 'error',
        title: 'Backend stopped',
        message: 'The Book Organizer backend stopped unexpectedly.',
        detail:
          'Sorting and library loading will not work until the app is restarted. ' +
          `Exit code: ${code === null ? signal : code}`,
        buttons: ['OK'],
      });
    }
  });
}

async function isBackendReachable() {
  try {
    const res = await fetch(`${BACKEND_URL}/api/health`);
    return res.ok;
  } catch {
    return false;
  }
}

async function startBackend() {
  // Someone may already be running the backend by hand (the documented dev workflow), in which
  // case starting a second one would just fail on the port and confuse the logs.
  if (await isBackendReachable()) {
    console.log('[API] Backend already running');
    return;
  }

  if (isDev()) {
    console.log('[API] Starting backend via dotnet run...');
    backendProcess = spawn(
      'dotnet',
      ['run', '--project', path.join(__dirname, '../../ManagerApi/ManagerApi.csproj')],
      { stdio: 'pipe' }
    );
  } else {
    const exe = getBackendExecutable();
    console.log(`[API] Starting backend: ${exe}`);

    if (!fs.existsSync(exe)) {
      backendExit = { code: null, signal: null, error: `Backend executable not found at ${exe}` };
      return;
    }

    if (process.platform !== 'win32') {
      try {
        fs.chmodSync(exe, 0o755);
      } catch {
        // Best effort; the spawn below reports the real problem if it is not executable.
      }
    }

    backendProcess = spawn(exe, [], { stdio: 'pipe', env: { ...process.env } });
  }

  attachProcessLogging(backendProcess);
}

async function waitForBackend() {
  const deadline = Date.now() + BACKEND_START_TIMEOUT_MS;

  while (Date.now() < deadline) {
    if (await isBackendReachable()) return true;

    // No point waiting out the full timeout for a process that has already died.
    if (backendExit) return false;

    await new Promise((resolve) => setTimeout(resolve, BACKEND_POLL_INTERVAL_MS));
  }

  return false;
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1320,
    height: 860,
    // The layout sheds the sidebar labels and the optional table columns below this, so the
    // window stays usable rather than needing a horizontal scrollbar.
    minWidth: 720,
    minHeight: 560,
    frame: false,
    backgroundColor: '#0c0e11',
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
    },
  });

  if (isDev()) {
    mainWindow.loadURL('http://localhost:5173');
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  mainWindow.once('ready-to-show', () => mainWindow?.show());
  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

function withWindow(action) {
  return () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      return action(mainWindow);
    }
    return null;
  };
}

function registerIpcHandlers() {
  ipcMain.handle('dialog:openFile', async (_, filters) => {
    if (!mainWindow || mainWindow.isDestroyed()) return null;

    const result = await dialog.showOpenDialog(mainWindow, {
      properties: ['openFile'],
      filters: filters || [{ name: 'CSV Files', extensions: ['csv'] }],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('dialog:openFolder', async () => {
    if (!mainWindow || mainWindow.isDestroyed()) return null;

    const result = await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory'] });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('window:minimize', withWindow((window) => window.minimize()));
  ipcMain.handle(
    'window:maximize',
    withWindow((window) => (window.isMaximized() ? window.unmaximize() : window.maximize()))
  );
  ipcMain.handle('window:close', withWindow((window) => window.close()));
  ipcMain.handle('window:isMaximized', withWindow((window) => window.isMaximized()));
}

// Two copies of the app would both try to own port 5123, and the loser would look broken.
if (!app.requestSingleInstanceLock()) {
  app.quit();
} else {
  app.on('second-instance', () => {
    if (mainWindow && !mainWindow.isDestroyed()) {
      if (mainWindow.isMinimized()) mainWindow.restore();
      mainWindow.focus();
    }
  });

  app.whenReady().then(async () => {
    registerIpcHandlers();
    await startBackend();

    console.log('Waiting for backend...');
    const backendReady = await waitForBackend();

    createWindow();

    if (!backendReady) {
      console.error('Backend did not start in time');
      dialog.showMessageBox(mainWindow, {
        type: 'error',
        title: 'Backend did not start',
        message: 'The Book Organizer backend could not be started.',
        detail:
          backendExit?.error ||
          'Check that port 5123 is free and that no other copy of the app is running, then restart the app.',
        buttons: ['OK'],
      });
    }
  });
}

function killBackend() {
  if (!backendProcess) return;

  const child = backendProcess;
  backendProcess = null;

  try {
    if (process.platform === 'win32') {
      spawn('taskkill', ['/pid', String(child.pid), '/f', '/t']);
    } else {
      child.kill('SIGTERM');
    }
  } catch {
    // Best effort.
  }
}

app.on('window-all-closed', () => {
  quitting = true;
  killBackend();
  app.quit();
});

app.on('before-quit', () => {
  quitting = true;
  killBackend();
});
