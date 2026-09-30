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

  beforeEach(() => {
    root = fs.mkdtempSync(path.join(os.tmpdir(), 'login-item-'));
    autostartFile = path.join(root, 'appData', 'autostart', 'openaudible-book-organizer.desktop');
    Object.defineProperty(process, 'platform', { value: 'linux' });
    process.env.APPIMAGE = '/opt/Organizer.AppImage';
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
});
