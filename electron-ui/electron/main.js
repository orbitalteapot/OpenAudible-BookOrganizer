const {
  app,
  BrowserWindow,
  Menu,
  Notification,
  Tray,
  dialog,
  ipcMain,
  nativeImage,
  nativeTheme,
  powerMonitor,
} = require('electron');
const path = require('path');
const fs = require('fs');
const backend = require('./backend');
const { HIDDEN_ARG, launchedAtLogin, setOpenAtLogin } = require('./login-item');

const APP_NAME = 'OpenAudible Book Organizer';
const THEMES = new Set(['system', 'light', 'dark']);
// The page's canvas colour in each theme, so the window never flashes the wrong one before it paints.
const WINDOW_BACKGROUND = { dark: '#0c0e11', light: '#f7f8fa' };
// How long "Stop sorting and quit" waits for the sort to wind down before quitting anyway.
const CANCEL_WAIT_MS = 5_000;
// Written once the "still running in the tray" notice has been shown, so it is shown only once.
const TRAY_NOTICE_MARKER = 'tray-notice-shown';

let mainWindow = null;
let tray = null;
let quitting = false;
let quitPromptOpen = false;
// The saved "Keep running in the background when the window is closed" setting.
let keepRunningInBackground = false;
// Chosen in the "A sort is still running" prompt; lasts until the app quits.
let keepRunningThisSession = false;
// The window was hidden on purpose (closed to the tray, or started at sign-in).
let hiddenInTray = false;

function isDev() {
  return !app.isPackaged;
}

function hasWindow() {
  return mainWindow !== null && !mainWindow.isDestroyed();
}

function windowBackground() {
  return nativeTheme.shouldUseDarkColors ? WINDOW_BACKGROUND.dark : WINDOW_BACKGROUND.light;
}

function syncWindowBackground() {
  if (hasWindow()) mainWindow.setBackgroundColor(windowBackground());
}

/** A message box over the window when the user can see it, free-standing when it is in the tray. */
function showMessage(options) {
  return hasWindow() && mainWindow.isVisible()
    ? dialog.showMessageBox(mainWindow, options)
    : dialog.showMessageBox(options);
}

function createWindow({ reveal }) {
  mainWindow = new BrowserWindow({
    width: 1320,
    height: 860,
    // The layout sheds the sidebar labels and the optional table columns below this, so the
    // window stays usable rather than needing a horizontal scrollbar.
    minWidth: 720,
    minHeight: 560,
    frame: false,
    backgroundColor: windowBackground(),
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true,
      // The port is picked at launch, so the page cannot know it in advance. preload.js reads this.
      additionalArguments: [`--backend-url=${backend.url() ?? ''}`],
    },
  });

  if (isDev()) {
    mainWindow.loadURL('http://localhost:5173');
  } else {
    mainWindow.loadFile(path.join(__dirname, '../dist/index.html'));
  }

  if (reveal) mainWindow.once('ready-to-show', () => mainWindow?.show());
  mainWindow.on('close', onWindowClose);
  mainWindow.on('closed', () => {
    mainWindow = null;
  });
  // Windows is signing out or shutting down: never hold that up with a prompt.
  mainWindow.on('session-end', () => {
    quitting = true;
  });

  // The window is frameless, so the title bar draws its own maximise/restore glyph. Snapping the
  // window with an OS shortcut changes the state without going through our IPC, and the glyph
  // would keep showing the previous one until the button was clicked.
  const publishMaximized = () => {
    if (hasWindow()) {
      mainWindow.webContents.send('window:maximized-changed', mainWindow.isMaximized());
    }
  };
  mainWindow.on('maximize', publishMaximized);
  mainWindow.on('unmaximize', publishMaximized);
}

function showWindow() {
  hiddenInTray = false;
  if (hasWindow()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  }
  updateTray();
}

function hideToTray() {
  hiddenInTray = true;
  updateTray();
  if (hasWindow()) mainWindow.hide();
  announceTrayOnce();
}

function staysInBackground() {
  return keepRunningInBackground || keepRunningThisSession;
}

// ---------------------------------------------------------------------------------------------
// Tray

function createTray() {
  // macOS recolours a "...Template" image for the menu bar; elsewhere the icon brings its own colour.
  // Either way nativeImage picks up the @2x file on high-density screens.
  const iconFile = process.platform === 'darwin' ? 'trayTemplate.png' : 'tray.png';
  const created = new Tray(nativeImage.createFromPath(path.join(__dirname, 'assets', iconFile)));

  created.setToolTip(APP_NAME);
  created.setContextMenu(
    Menu.buildFromTemplate([
      { label: 'Open Book Organizer', click: () => showWindow() },
      { type: 'separator' },
      { label: 'Quit', click: () => quitWhenSafe() },
    ])
  );
  created.on('click', () => showWindow());
  return created;
}

/** The tray icon is there while the app may be running with no window to show for it. */
function updateTray() {
  const wanted = staysInBackground() || hiddenInTray;
  if (wanted && !tray) {
    tray = createTray();
  } else if (!wanted && tray) {
    tray.destroy();
    tray = null;
  }
}

/** Tells the user, once ever, that closing the window did not quit the app. */
function announceTrayOnce() {
  const marker = path.join(app.getPath('userData'), TRAY_NOTICE_MARKER);
  if (fs.existsSync(marker)) return;

  try {
    fs.writeFileSync(marker, '');
  } catch {
    // Worst case the notice shows again next time.
  }

  const where = process.platform === 'darwin' ? 'menu bar' : 'system tray';
  const body = `Book Organizer is still running in the ${where}. Use its icon there to open it again or to quit.`;
  if (process.platform === 'win32' && tray) {
    // Works without the app ID that Windows toast notifications need, so it also shows in development.
    tray.displayBalloon({ title: APP_NAME, content: body });
  } else if (Notification.isSupported()) {
    new Notification({ title: APP_NAME, body }).show();
  }
}

// ---------------------------------------------------------------------------------------------
// Closing and quitting

function onWindowClose(event) {
  if (quitting) return;

  event.preventDefault();
  if (staysInBackground()) {
    hideToTray();
  } else {
    quitWhenSafe();
  }
}

/** Asks what to do about the running sort: 'keep', 'stop' or 'cancel'. */
async function askAboutRunningSort() {
  const { response } = await showMessage({
    type: 'question',
    title: APP_NAME,
    message: 'A sort is still running',
    detail: 'Keep it running in the background and it finishes on its own, or stop it now and quit.',
    buttons: ['Keep running in the background', 'Stop sorting and quit', 'Cancel'],
    defaultId: 0,
    cancelId: 2,
    noLink: true,
  });
  return ['keep', 'stop', 'cancel'][response];
}

/** Quits, but asks first when a sort is running, because quitting stops it partway through. */
async function quitWhenSafe() {
  // A second click on close or Quit while the question is up must not ask twice.
  if (quitPromptOpen) return;
  quitPromptOpen = true;

  try {
    if (await backend.isSortRunning()) {
      const choice = await askAboutRunningSort();
      if (choice === 'cancel') return;
      if (choice === 'keep') {
        keepRunningThisSession = true;
        hideToTray();
        return;
      }
      await backend.cancelSortAndWait(CANCEL_WAIT_MS);
    }

    quitting = true;
    app.quit();
  } finally {
    quitPromptOpen = false;
  }
}

// ---------------------------------------------------------------------------------------------
// Settings from the page and the backend

function applyBackgroundOptions(options) {
  keepRunningInBackground = Boolean(options?.keepRunningInBackground);
  setOpenAtLogin(Boolean(options?.openAtLogin));
  updateTray();
}

/** So the tray and the sign-in entry match the saved settings before the page has even loaded. */
async function applySavedBackgroundOptions() {
  const settings = await backend.request('/api/settings');
  if (settings) applyBackgroundOptions(settings);
}

function registerIpcHandlers() {
  ipcMain.handle('dialog:openFile', async (_, filters) => {
    if (!hasWindow()) return null;

    const result = await dialog.showOpenDialog(mainWindow, {
      properties: ['openFile'],
      filters: filters || [{ name: 'CSV Files', extensions: ['csv'] }],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('dialog:openFolder', async () => {
    if (!hasWindow()) return null;

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

  ipcMain.handle('app:setBackgroundOptions', (_, options) => applyBackgroundOptions(options));
  ipcMain.handle('app:setTheme', (_, theme) => {
    if (!THEMES.has(theme)) return;
    nativeTheme.themeSource = theme;
    syncWindowBackground();
  });
}

function withWindow(action) {
  return () => (hasWindow() ? action(mainWindow) : null);
}

// ---------------------------------------------------------------------------------------------
// Backend problems

function reportBackendStopped(reason) {
  if (quitting || !hasWindow()) return;

  // It may have died while the app sat in the tray; the user needs to see this either way.
  showWindow();
  showMessage({
    type: 'error',
    title: 'Backend stopped',
    message: 'The Book Organizer backend stopped unexpectedly.',
    detail: `Sorting and library loading will not work until the app is restarted. ${reason}`,
    buttons: ['OK'],
  });
}

function reportBackendDidNotStart() {
  console.error('Backend did not start in time');
  showMessage({
    type: 'error',
    title: 'Backend did not start',
    message: 'The Book Organizer backend could not be started.',
    detail:
      backend.failure() ||
      'It did not answer within a minute. Restart the app; if this keeps happening, reinstall it.',
    buttons: ['OK'],
  });
}

// ---------------------------------------------------------------------------------------------
// Startup

// One copy owns the tray, the schedule and the settings file; a second launch just brings it forward.
if (!app.requestSingleInstanceLock()) {
  // Nothing is running in this copy, so there is nothing to ask about.
  quitting = true;
  app.quit();
} else {
  app.on('second-instance', (_event, argv) => {
    // The sign-in entry firing while the app already runs should not pop the window up.
    if (!argv.includes(HIDDEN_ARG)) showWindow();
  });

  // macOS: clicking the Dock icon brings back a window that was closed to the menu bar.
  app.on('activate', () => showWindow());

  app.whenReady().then(async () => {
    registerIpcHandlers();
    nativeTheme.on('updated', syncWindowBackground);
    // macOS and Linux are shutting down: never hold that up with a prompt.
    powerMonitor.on('shutdown', () => {
      quitting = true;
    });

    await backend.start({
      packaged: app.isPackaged,
      settingsPath: path.join(app.getPath('userData'), 'settings.json'),
      onUnexpectedExit: reportBackendStopped,
    });

    console.log('Waiting for backend...');
    const backendReady = await backend.waitUntilReady();
    if (backendReady) await applySavedBackgroundOptions();

    // Started at sign-in: wait in the tray. If the backend failed, show the window anyway so the
    // error is not lost.
    const startHidden = backendReady && launchedAtLogin();
    createWindow({ reveal: !startHidden });

    if (startHidden) {
      hiddenInTray = true;
      updateTray();
    }
    if (!backendReady) reportBackendDidNotStart();
  });
}

app.on('window-all-closed', () => {
  quitting = true;
  backend.stop();
  app.quit();
});

app.on('before-quit', (event) => {
  // Cmd+Q, Dock Quit and the default menu's Quit call app.quit() directly; send them through the
  // same "A sort is still running" question as every other quit.
  if (!quitting) {
    event.preventDefault();
    quitWhenSafe();
    return;
  }
  backend.stop();
});
