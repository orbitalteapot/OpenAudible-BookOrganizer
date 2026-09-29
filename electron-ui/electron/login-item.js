/**
 * "Start when I sign in": registering the app with the OS, and recognising a launch at sign-in.
 */
const { app } = require('electron');
const fs = require('fs');
const path = require('path');
const { desktopName } = require('../package.json');

/** Passed by the sign-in entry, so the app starts quietly in the tray rather than opening a window. */
const HIDDEN_ARG = '--hidden';

/** Whether this launch came from the sign-in entry. Only meaningful for the first instance. */
function launchedAtLogin() {
  if (process.argv.includes(HIDDEN_ARG)) return true;
  // macOS login items cannot carry arguments, but the OS says when it launched us.
  return process.platform === 'darwin' && app.getLoginItemSettings().wasOpenedAtLogin === true;
}

/** Quotes one argument for the Exec line of a .desktop file (the spec's quoting, then its string escaping). */
function quoteDesktopExecArg(arg) {
  return `"${arg.replace(/(["`$\\])/g, '\\$1')}"`.replace(/\\/g, '\\\\');
}

/** Electron's login items cover Windows and macOS only; Linux desktops read XDG autostart entries. */
function setLinuxAutostart(enabled) {
  const file = path.join(app.getPath('appData'), 'autostart', `${desktopName}.desktop`);
  if (!enabled) {
    fs.rmSync(file, { force: true });
    return;
  }

  // An AppImage runs from a mount point that changes every launch; APPIMAGE is the file itself.
  const executable = process.env.APPIMAGE || process.execPath;
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(
    file,
    [
      '[Desktop Entry]',
      'Type=Application',
      `Name=${app.getName()}`,
      `Exec=${quoteDesktopExecArg(executable)} ${HIDDEN_ARG}`,
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

  try {
    if (process.platform === 'linux') {
      setLinuxAutostart(enabled);
    } else {
      app.setLoginItemSettings({ openAtLogin: enabled, args: [HIDDEN_ARG] });
    }
  } catch (err) {
    console.error('[login] Could not change "Start when I sign in":', err.message);
  }
}

module.exports = { HIDDEN_ARG, launchedAtLogin, setOpenAtLogin };
