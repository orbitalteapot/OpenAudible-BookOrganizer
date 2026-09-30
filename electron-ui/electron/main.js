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
  shell,
} = require('electron');
const path = require('path');
const { pathToFileURL } = require('url');
const fs = require('fs');
const { APP_NAME } = require('./app-info');
const backend = require('./backend');
const { HIDDEN_ARG, launchedAtLogin, ownAppImage, setOpenAtLogin } = require('./login-item');

const DEV_SERVER_URL = 'http://localhost:5173';
const THEMES = new Set(['system', 'light', 'dark']);
// The page's canvas colour in each theme, so the window never flashes the wrong one before it paints.
const WINDOW_BACKGROUND = { dark: '#0c0e11', light: '#f7f8fa' };
// How long "Stop sorting and quit" waits for the sort to wind down before quitting anyway.
const CANCEL_WAIT_MS = 5_000;
// Written once the "still running in the tray" notice has been shown, so it is shown only once.
const TRAY_NOTICE_MARKER = 'tray-notice-shown';
// A window that stops working again this soon after being reloaded is not reloaded again unasked.
const RECRASH_MS = 30_000;

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
// Someone asked for the window (a second launch, the Dock) before startup had created it.
let showRequestedBeforeWindow = false;
// When the window's page last stopped working.
let lastPageCrash = 0;
// The backend has answered once. Until then a failure is reported as "did not start", not "stopped".
let backendReady = false;

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
    },
  });

  mainWindow.loadURL(appUrl());
  keepWindowOnTheApp(mainWindow.webContents);
  mainWindow.webContents.on('render-process-gone', (_event, details) => onPageGone(details));
  // The title bar shows Quit while closing the window keeps the app running; it asks once it loads.
  mainWindow.webContents.on('did-finish-load', publishStaysInBackground);

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

/**
 * The window only ever shows the app. A file dropped on it would otherwise replace the app with that
 * file (and the frameless window has no way back), and any page loaded there would get the preload
 * API: the file dialogs and the sign-in setting. Web links open in the browser instead.
 */
function keepWindowOnTheApp(webContents) {
  webContents.on('will-navigate', (event, url) => {
    if (!isAppPage(url)) event.preventDefault();
  });
  webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:\/\//i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
}

/**
 * The window is frameless, so its title bar and buttons are part of the page: a page that crashed
 * (out of memory, a GPU process failure) leaves a blank pane nobody can move, close or reload. It is
 * loaded again at once; if it stops working again straight away, the user chooses what happens.
 */
async function onPageGone({ reason }) {
  if (quitting || reason === 'clean-exit' || !hasWindow()) return;

  console.error(`The window's page stopped working (${reason})`);
  const again = Date.now() - lastPageCrash < RECRASH_MS;
  lastPageCrash = Date.now();
  if (again) {
    const { response } = await showMessage({
      type: 'error',
      title: APP_NAME,
      message: 'The Book Organizer window stopped working.',
      detail: 'A sort that is running carries on. Reload the window to continue, or quit the app.',
      buttons: ['Reload', 'Quit'],
      defaultId: 0,
      cancelId: 0,
      noLink: true,
    });
    // Quit can still be called off ("A sort is still running": Cancel, or Keep running), or be
    // asked already; the window must not be left blank, with no title bar to close it, when it is.
    if (response === 1 && (await quitWhenSafe())) return;
  }

  if (hasWindow()) mainWindow.loadURL(appUrl());
}

/** The page the window shows: the Vite dev server in development, the built page otherwise. */
function appUrl() {
  return isDev() ? DEV_SERVER_URL : pathToFileURL(path.join(__dirname, '../dist/index.html')).href;
}

/** `url` is the app's own page, reloaded or with a different #fragment. */
function isAppPage(url) {
  try {
    const page = new URL(appUrl());
    const target = new URL(url);
    return target.protocol === page.protocol && target.host === page.host && target.pathname === page.pathname;
  } catch {
    return false;
  }
}

function showWindow() {
  hiddenInTray = false;
  if (hasWindow()) {
    if (mainWindow.isMinimized()) mainWindow.restore();
    mainWindow.show();
    mainWindow.focus();
  } else {
    showRequestedBeforeWindow = true;
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
  publishStaysInBackground();
}

/**
 * Tells the page whether closing the window keeps the app running, so its title bar can offer Quit.
 * The tray is not enough: stock GNOME shows no tray icons at all, and without a Quit in the window an
 * app told to keep running could then not be quit for the rest of the session.
 */
function publishStaysInBackground() {
  if (hasWindow()) mainWindow.webContents.send('window:stays-in-background-changed', staysInBackground());
}

/**
 * What the notice says about finding the app again. Linux desktops may show no tray icon (GNOME
 * without an extension), so there it does not promise one.
 */
function trayNotice() {
  if (process.platform === 'darwin') {
    return 'Book Organizer is still running in the menu bar. Use its icon there to open it again or to quit.';
  }
  if (process.platform === 'win32') {
    return 'Book Organizer is still running in the system tray. Use its icon there to open it again or to quit.';
  }
  return (
    'Book Organizer is still running in the background. Open it again from your apps (or its tray icon, ' +
    'if your desktop shows one) to bring the window back, and use Quit in its title bar to stop it.'
  );
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

  const body = trayNotice();
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

/**
 * Quits, but asks first when a sort is running, because quitting stops it partway through. Resolves
 * to whether the app is quitting: false when the user kept it running or called it off.
 */
async function quitWhenSafe() {
  // A second click on close or Quit while the question is up must not ask twice.
  if (quitPromptOpen) return false;
  quitPromptOpen = true;

  try {
    if (await backend.isSortRunning()) {
      const choice = await askAboutRunningSort();
      if (choice === 'cancel') return false;
      if (choice === 'keep') {
        keepRunningThisSession = true;
        hideToTray();
        return false;
      }
      await backend.cancelSortAndWait(CANCEL_WAIT_MS);
    }

    quitting = true;
    app.quit();
    return true;
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

/**
 * What the page may say about a file or folder dialog: what it is for (the title, and the message
 * macOS shows in the sheet), the button that accepts, and where it opens. Only strings are taken.
 */
function pickerOptions(request) {
  const options = {};
  for (const key of ['title', 'message', 'buttonLabel', 'defaultPath']) {
    if (typeof request?.[key] === 'string' && request[key]) options[key] = request[key];
  }
  return options;
}

function registerIpcHandlers() {
  ipcMain.handle('dialog:openFile', async (_, request) => {
    if (!hasWindow()) return null;

    const result = await dialog.showOpenDialog(mainWindow, {
      ...pickerOptions(request),
      properties: ['openFile'],
      filters: Array.isArray(request?.filters) ? request.filters : [{ name: 'CSV Files', extensions: ['csv'] }],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('dialog:openFolder', async (_, request) => {
    if (!hasWindow()) return null;

    // createDirectory: macOS only offers "New Folder" with it, and a first destination is often a
    // folder that has yet to be made.
    const result = await dialog.showOpenDialog(mainWindow, {
      ...pickerOptions(request),
      properties: ['openDirectory', 'createDirectory'],
    });
    return result.canceled ? null : result.filePaths[0];
  });

  ipcMain.handle('window:minimize', withWindow((window) => window.minimize()));
  ipcMain.handle(
    'window:maximize',
    withWindow((window) => (window.isMaximized() ? window.unmaximize() : window.maximize()))
  );
  ipcMain.handle('window:close', withWindow((window) => window.close()));
  ipcMain.handle('window:isMaximized', withWindow((window) => window.isMaximized()));
  ipcMain.handle('window:staysInBackground', () => staysInBackground());

  ipcMain.handle('app:quit', () => quitWhenSafe());
  // The port is picked at launch, and again whenever the backend is restarted, so the page cannot
  // know it in advance; preload.js asks each time the page loads.
  ipcMain.on('backend:url', (event) => {
    event.returnValue = backend.url() ?? '';
  });
  ipcMain.handle('backend:stopped', () => backendStoppedReason());
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

/** Why the backend has stopped with nothing left to start it again, or null while it runs or restarts. */
function backendStoppedReason() {
  return backend.stoppedForGood() ? backend.failure() : null;
}

/**
 * The backend exited and was started again on a new port. The page only learns the port as it loads
 * (see preload.js), so it is loaded again, and picks up where the new backend is.
 */
function onBackendRestarted() {
  if (hasWindow()) mainWindow.loadURL(appUrl());
}

/**
 * Quits and opens the app again. Only asked for once the backend has stopped for good, so there is
 * no sort to ask about. Never hidden in the tray, even when this launch was a sign-in start. An
 * AppImage is started again from its file: the program Electron would restart lives in the AppImage's
 * mount, which is gone by the time the relauncher, waiting for this copy to exit, starts it.
 */
function relaunchApp() {
  quitting = true;
  const appImage = ownAppImage();
  app.relaunch({
    ...(appImage && { execPath: appImage }),
    args: process.argv.slice(1).filter((arg) => arg !== HIDDEN_ARG),
  });
  app.quit();
}

/**
 * Opening the app again is how people restart it, and what the messages about a backend that stopped
 * ask for; with the window closed to the tray, it would otherwise bring back this same copy, still
 * without the backend and without automatic sorting. Windows and Linux start a second copy for it,
 * macOS reopens this one (see 'activate').
 */
function onOpenedAgain({ hidden = false } = {}) {
  if (backendStoppedReason()) {
    relaunchApp();
    return;
  }
  // The sign-in entry firing while the app already runs should not pop the window up.
  if (!hidden) showWindow();
}

/** The backend was left stopped: the page stops saying it is still trying, and the user is told. */
function onBackendStoppedForGood(reason) {
  if (hasWindow()) mainWindow.webContents.send('backend:stopped', reason);
  reportBackendStopped(reason);
}

// Worded as the page words it: the user knows "Book Organizer", not a backend or an exit code.
async function reportBackendStopped(reason) {
  // Before it first answered, startup reports the failure itself (reportBackendDidNotStart).
  if (quitting || !hasWindow() || !backendReady) return;

  // It may have died while the app sat in the tray; the user needs to see this either way.
  showWindow();
  await offerRestart({
    title: 'Book Organizer stopped working',
    message: 'Book Organizer stopped working.',
    detail: `Sorting and your library are unavailable until the app is restarted. ${reason}`,
  });
}

function reportBackendDidNotStart() {
  console.error('Backend did not start in time');
  const stopped = backendStoppedReason();
  if (stopped && hasWindow()) mainWindow.webContents.send('backend:stopped', stopped);
  return offerRestart({
    title: "Book Organizer didn't start",
    message: "Book Organizer didn't start.",
    detail:
      backend.failure() ||
      'It did not respond within a minute. Restart the app; if this keeps happening, reinstall it.',
  });
}

/** An error message box whose Restart button opens the app again. */
async function offerRestart({ title, message, detail }) {
  const { response } = await showMessage({
    type: 'error',
    title,
    message,
    detail,
    buttons: ['Restart', 'Not now'],
    defaultId: 0,
    cancelId: 1,
    noLink: true,
  });
  if (response === 0 && !quitting) relaunchApp();
}

// ---------------------------------------------------------------------------------------------
// Startup

// One copy owns the tray, the schedule and the settings file; a second launch just brings it forward.
if (!app.requestSingleInstanceLock()) {
  // Nothing is running in this copy, so there is nothing to ask about.
  quitting = true;
  app.quit();
} else {
  app.on('second-instance', (_event, argv) => onOpenedAgain({ hidden: argv.includes(HIDDEN_ARG) }));

  // macOS: opening the app from the Dock, Launchpad or Finder while it runs is not a second copy but
  // this event, which also brings back a window that was closed to the menu bar.
  app.on('activate', () => onOpenedAgain());

  app.whenReady().then(async () => {
    registerIpcHandlers();
    nativeTheme.on('updated', syncWindowBackground);
    // macOS and Linux are shutting down: never hold that up with a prompt.
    powerMonitor.on('shutdown', () => {
      quitting = true;
    });
    // Ctrl+C in the launching terminal or a service manager stopping the app: never hold that up
    // with a prompt. The signal usually reaches the backend too, and it is already cancelling its
    // run. Registered once ready, because Electron replaces earlier handlers with its own during
    // startup, and that one quits through before-quit's prompt.
    for (const signal of ['SIGINT', 'SIGTERM']) {
      process.on(signal, () => {
        quitting = true;
        app.quit();
      });
    }

    await backend.start({
      packaged: app.isPackaged,
      settingsPath: path.join(app.getPath('userData'), 'settings.json'),
      onRestarted: onBackendRestarted,
      onStoppedForGood: onBackendStoppedForGood,
    });

    // The window comes up at once, without waiting for the backend: it can take a while to answer
    // (the copy that just quit still letting go of the settings file, a virus scan of a new install),
    // and the page says "Starting the organizer…" meanwhile instead of nothing appearing at all.
    // Started at sign-in, it waits in the tray, unless the user opened the app in the meantime.
    const startHidden = launchedAtLogin() && !showRequestedBeforeWindow;
    createWindow({ reveal: !startHidden });
    if (startHidden) {
      hiddenInTray = true;
      updateTray();
    }

    console.log('Waiting for backend...');
    backendReady = await backend.waitUntilReady();
    if (backendReady) {
      await applySavedBackgroundOptions();
    } else {
      // Shown even after a sign-in start, or the problem would sit unseen in the tray.
      showWindow();
      reportBackendDidNotStart();
    }
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
