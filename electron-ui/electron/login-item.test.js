// @vitest-environment node
import fs from 'node:fs';
import { createRequire } from 'node:module';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);

/** login-item.js as the main process loads it, with a stand-in for Electron's `app`. */
function loadLoginItem(app) {
  const electronPath = require.resolve('electron');
  require.cache[electronPath] = { id: electronPath, filename: electronPath, loaded: true, exports: { app } };
  delete require.cache[require.resolve('./login-item')];
  return require('./login-item');
}

describe('setOpenAtLogin', () => {
  let root;
  let autostartFile;
  let setOpenAtLogin;
  const platform = process.platform;
  const appImage = process.env.APPIMAGE;
  const appDir = process.env.APPDIR;
  const execPath = process.execPath;

  beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'login-item-'));
    autostartFile = path.join(root, 'appData', 'autostart', 'openaudible-book-organizer.desktop');
    Object.defineProperty(process, 'platform', { value: 'linux' });
    // What an AppImage's runtime sets up: the file, the folder it is mounted on, and the app inside.
    process.env.APPIMAGE = '/opt/Organizer.AppImage';
    process.env.APPDIR = '/tmp/.mount_Organizer';
    Object.defineProperty(process, 'execPath', { value: '/tmp/.mount_Organizer/openaudible-book-organizer', configurable: true, writable: true });
    ({ setOpenAtLogin } = loadLoginItem({
      isPackaged: true,
      getPath: (name) => path.join(root, name),
      setLoginItemSettings: vi.fn(),
    }));
    fs.mkdirSync(path.join(root, 'userData'));
  });

  afterEach(() => {
    Object.defineProperty(process, 'platform', { value: platform });
    if (appImage === undefined) delete process.env.APPIMAGE;
    else process.env.APPIMAGE = appImage;
    if (appDir === undefined) delete process.env.APPDIR;
    else process.env.APPDIR = appDir;
    Object.defineProperty(process, 'execPath', { value: execPath, configurable: true, writable: true });
    fs.rmSync(root, { recursive: true, force: true });
  });

  it('leaves an entry the person removed in their desktop settings alone at the next launch', () => {
    setOpenAtLogin(true);
    expect(fs.readFileSync(autostartFile, 'utf8')).toContain('Exec="/opt/Organizer.AppImage" --hidden');

    // Removed in GNOME Tweaks' Startup Applications; the app is opened again with the setting still on.
    fs.rmSync(autostartFile);
    setOpenAtLogin(true);
    expect(fs.existsSync(autostartFile)).toBe(false);

    // Turning it off and on again in the app is asking for it back.
    setOpenAtLogin(false);
    setOpenAtLogin(true);
    expect(fs.existsSync(autostartFile)).toBe(true);
  });

  it('registers the entry again when the app has moved', () => {
    setOpenAtLogin(true);
    process.env.APPIMAGE = '/home/me/Apps/Organizer.AppImage';
    setOpenAtLogin(true);

    expect(fs.readFileSync(autostartFile, 'utf8')).toContain('Exec="/home/me/Apps/Organizer.AppImage" --hidden');
  });

  it('registers the app itself, not another AppImage it was started from', () => {
    // Installed from the .deb, started in the terminal of an editor that runs as an AppImage.
    process.env.APPIMAGE = '/home/me/Apps/Cursor.AppImage';
    process.env.APPDIR = '/tmp/.mount_Cursor';
    Object.defineProperty(process, 'execPath', { value: '/opt/Organizer/openaudible-book-organizer', configurable: true, writable: true });

    setOpenAtLogin(true);

    expect(fs.readFileSync(autostartFile, 'utf8')).toContain('Exec="/opt/Organizer/openaudible-book-organizer" --hidden');
  });

  it('names the program the entry needs, so the desktop skips it once the app is uninstalled', () => {
    // The .deb installs where the product name, spaces and all, says.
    Object.defineProperty(process, 'execPath', {
      value: '/opt/OpenAudible Book Organizer/openaudible-book-organizer',
      configurable: true,
      writable: true,
    });
    delete process.env.APPIMAGE;
    delete process.env.APPDIR;

    setOpenAtLogin(true);

    expect(fs.readFileSync(autostartFile, 'utf8').split('\n')).toContain(
      'TryExec=/opt/OpenAudible Book Organizer/openaudible-book-organizer'
    );
  });
});

describe('ownAppImage', () => {
  const appImage = process.env.APPIMAGE;
  const appDir = process.env.APPDIR;
  const execPath = process.execPath;

  afterEach(() => {
    if (appImage === undefined) delete process.env.APPIMAGE;
    else process.env.APPIMAGE = appImage;
    if (appDir === undefined) delete process.env.APPDIR;
    else process.env.APPDIR = appDir;
    Object.defineProperty(process, 'execPath', { value: execPath, configurable: true, writable: true });
  });

  it('is the AppImage file, which Restart starts again because the mount the app runs from goes away when it quits', () => {
    process.env.APPIMAGE = '/opt/Organizer.AppImage';
    process.env.APPDIR = '/tmp/.mount_Organizer';
    Object.defineProperty(process, 'execPath', { value: '/tmp/.mount_Organizer/openaudible-book-organizer', configurable: true, writable: true });

    expect(loadLoginItem({}).ownAppImage()).toBe('/opt/Organizer.AppImage');
  });

  it('is null for an install started from another AppImage', () => {
    process.env.APPIMAGE = '/home/me/Apps/Cursor.AppImage';
    process.env.APPDIR = '/tmp/.mount_Cursor';
    Object.defineProperty(process, 'execPath', { value: '/opt/Organizer/openaudible-book-organizer', configurable: true, writable: true });

    expect(loadLoginItem({}).ownAppImage()).toBeNull();
  });
});
