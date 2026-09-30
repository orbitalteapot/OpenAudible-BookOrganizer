/**
 * "Start when I sign in": registering the app with the OS, and recognising a launch at sign-in.
 */
const { app } = require('electron');
const fs = require('fs');
const path = require('path');
const { desktopName } = require('../package.json');
const { APP_NAME } = require('./app-info');

/** Passed by the sign-in entry, so the app starts quietly in the tray rather than opening a window. */
const HIDDEN_ARG = '--hidden';

/** Whether this launch came from the sign-in entry. Only meaningful for the first instance. */
function launchedAtLogin() {
  if (process.argv.includes(HIDDEN_ARG)) return true;
  // macOS login items cannot carry arguments, but the OS says when it launched us.
  return process.platform === 'darwin' && app.getLoginItemSettings().wasOpenedAtLogin === true;
}

/** A .desktop file's escaping for a value of type string: backslashes and line breaks. */
function escapeDesktopString(value) {
  return value.replace(/\\/g, '\\\\').replace(/\n/g, '\\n').replace(/\r/g, '\\r').replace(/\t/g, '\\t');
}

/** Quotes one argument for the Exec line of a .desktop file (the spec's quoting, then its string escaping). */
function quoteDesktopExecArg(arg) {
  return escapeDesktopString(`"${arg.replace(/(["`$\\])/g, '\\$1')}"`);
}

/**
 * The AppImage file this app runs from, or null when it is not one. An AppImage runs from a mount
 * point (APPDIR) that changes every launch and goes away when the app quits; APPIMAGE is the file
 * itself. Both are inherited by everything an AppImage starts, so they are only this app's when it
 * runs from inside that mount: a .deb install started from another AppImage's terminal would
 * otherwise register (or restart as) that other app.
 */
function ownAppImage() {
  const { APPIMAGE, APPDIR } = process.env;
  const relative = APPDIR ? path.relative(APPDIR, process.execPath) : '';
  const runsFromAppImage = Boolean(APPIMAGE && relative && !relative.startsWith('..') && !path.isAbsolute(relative));
  return runsFromAppImage ? APPIMAGE : null;
}

/** What the sign-in entry starts: the program itself, never a path inside an AppImage's mount. */
function loginExecutable() {
  return ownAppImage() ?? process.execPath;
}

/**
 * Remembers what was last registered with the OS. The saved setting is applied at every launch, and
 * registering it again each time would undo a person turning the entry off in Windows' Startup apps
 * or their desktop's autostart settings, with nothing in the app to say why it came back.
 */
function appliedFile() {
  return path.join(app.getPath('userData'), 'login-item.json');
}

function readApplied() {
  try {
    return JSON.parse(fs.readFileSync(appliedFile(), 'utf8'));
  } catch {
    return null;
  }
}

/** Electron's login items cover Windows and macOS only; Linux desktops read XDG autostart entries. */
function setLinuxAutostart(enabled, executable) {
  const file = path.join(app.getPath('appData'), 'autostart', `${desktopName}.desktop`);
  if (!enabled) {
    fs.rmSync(file, { force: true });
    return;
  }

  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(
    file,
    [
      '[Desktop Entry]',
      'Type=Application',
      `Name=${APP_NAME}`,
      'Comment=Starts hidden in the tray, ready for the next automatic sort',
      `Exec=${quoteDesktopExecArg(executable)} ${HIDDEN_ARG}`,
      // The entry lives in the user's home, where removing the .deb or deleting the AppImage cannot
      // reach it; with this, the desktop skips it once the program is gone instead of trying to start
      // a missing file at every sign-in.
      `TryExec=${escapeDesktopString(executable)}`,
      'X-GNOME-Autostart-enabled=true',
      '',
    ].join('\n')
  );
}

function setOpenAtLogin(enabled) {
  // A development build would register the bare Electron binary, which opens an empty shell at sign-in.
  if (!app.isPackaged) {
    console.log(`[login] Not changing "Start when I sign in" (${enabled}) in a development build`);
    return;
  }

  // Registered again only when the setting changes, or the app has moved and the entry would start nothing.
  const executable = loginExecutable();
  const applied = readApplied();
  if (applied?.enabled === enabled && applied?.executable === executable) return;

  try {
    if (process.platform === 'linux') {
      setLinuxAutostart(enabled, executable);
    } else {
      app.setLoginItemSettings({ openAtLogin: enabled, args: [HIDDEN_ARG] });
    }
    fs.writeFileSync(appliedFile(), JSON.stringify({ enabled, executable }));
  } catch (err) {
    console.error('[login] Could not change "Start when I sign in":', err.message);
  }
}

module.exports = { HIDDEN_ARG, launchedAtLogin, ownAppImage, setOpenAtLogin };
