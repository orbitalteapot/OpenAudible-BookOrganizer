const { app, BrowserWindow, ipcMain, dialog } = require('electron');
const path = require('path');
const { spawn } = require('child_process');

let mainWindow;
let backendProcess;

function startBackend() {
  const isDev = !app.isPackaged;
  if (isDev) {
    // Check if backend is already running before starting
    fetch('http://localhost:5123/api/health')
      .then(() => {
        console.log('[API] Backend already running');
      })
      .catch(() => {
        console.log('[API] Starting backend...');
        backendProcess = spawn('dotnet', [
          'run', '--project',
          path.join(__dirname, '../../ManagerApi/ManagerApi.csproj')
        ], { stdio: 'pipe' });

        backendProcess.stdout.on('data', (data) => {
          console.log(`[API] ${data.toString().trim()}`);
        });

        backendProcess.stderr.on('data', (data) => {
          console.error(`[API] ${data.toString().trim()}`);
        });

        backendProcess.on('error', (err) => {
          console.error('Failed to start backend:', err.message);
        });
      });
  }
}

async function waitForBackend(maxRetries = 40) {
  for (let i = 0; i < maxRetries; i++) {
    try {
      const response = await fetch('http://localhost:5123/api/health');
      if (response.ok) return true;
    } catch {
      // Backend not ready yet
    }
    await new Promise((r) => setTimeout(r, 1000));
  }
  return false;
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1320,
    height: 860,
    minWidth: 960,
    minHeight: 640,
    frame: false,
    backgroundColor: '#0f172a',
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
    },
  });

  const isDev = !app.isPackaged;

  if (isDev) {
    mainWindow.loadURL('http://localhost:5173');
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  mainWindow.once('ready-to-show', () => {
    mainWindow.show();
  });
}

app.whenReady().then(async () => {
  startBackend();

  console.log('Waiting for backend...');
  const backendReady = await waitForBackend();
  if (!backendReady) {
    console.error('Backend did not start in time');
  }

  createWindow();

  // IPC: File dialog
  ipcMain.handle('dialog:openFile', async (_, filters) => {
    const result = await dialog.showOpenDialog(mainWindow, {
      properties: ['openFile'],
      filters: filters || [{ name: 'CSV Files', extensions: ['csv'] }],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  // IPC: Folder dialog
  ipcMain.handle('dialog:openFolder', async () => {
    const result = await dialog.showOpenDialog(mainWindow, {
      properties: ['openDirectory'],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  // IPC: Window controls
  ipcMain.handle('window:minimize', () => mainWindow.minimize());
  ipcMain.handle('window:maximize', () => {
    if (mainWindow.isMaximized()) {
      mainWindow.unmaximize();
    } else {
      mainWindow.maximize();
    }
  });
  ipcMain.handle('window:close', () => mainWindow.close());
  ipcMain.handle('window:isMaximized', () => mainWindow.isMaximized());
});

app.on('window-all-closed', () => {
  if (backendProcess) {
    backendProcess.kill();
  }
  app.quit();
});
