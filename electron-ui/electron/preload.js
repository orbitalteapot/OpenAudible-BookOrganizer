const { contextBridge, ipcRenderer } = require('electron');

// main.js picks the backend's port at launch, and a new one whenever it restarts the backend, when
// it loads the page again. Asked for as the page loads, so a reload always gets the current one.
const backendUrl = ipcRenderer.sendSync('backend:url');

contextBridge.exposeInMainWorld('electronAPI', {
  /** Where the backend listens, e.g. "http://127.0.0.1:49731". Empty if it could not be started. */
  backendUrl,

  /** Both take `{ title, message, buttonLabel, defaultPath }`; openFile also `filters`. */
  openFile: (options) => ipcRenderer.invoke('dialog:openFile', options),
  openFolder: (options) => ipcRenderer.invoke('dialog:openFolder', options),
  minimize: () => ipcRenderer.invoke('window:minimize'),
  maximize: () => ipcRenderer.invoke('window:maximize'),
  close: () => ipcRenderer.invoke('window:close'),
  isMaximized: () => ipcRenderer.invoke('window:isMaximized'),

  /** Whether closing the window keeps the app running (in the tray, or in the background). */
  staysInBackground: () => ipcRenderer.invoke('window:staysInBackground'),

  /** Fires when that changes. Returns an unsubscribe. */
  onStaysInBackgroundChanged: (handler) => {
    const listener = (_event, stays) => handler(stays);
    ipcRenderer.on('window:stays-in-background-changed', listener);
    return () => ipcRenderer.removeListener('window:stays-in-background-changed', listener);
  },

  /**
   * Why the organizer has stopped with nothing left to start it again (it kept exiting, or could not
   * be started at all), or null while it runs or is being restarted.
   */
  backendStopped: () => ipcRenderer.invoke('backend:stopped'),

  /** Fires with that reason once it happens. Returns an unsubscribe. */
  onBackendStopped: (handler) => {
    const listener = (_event, reason) => handler(reason);
    ipcRenderer.on('backend:stopped', listener);
    return () => ipcRenderer.removeListener('backend:stopped', listener);
  },

  /** Quits the app, asking first when a sort is running, as closing the window does. */
  quit: () => ipcRenderer.invoke('app:quit'),

  /** Fires when the window is maximised or restored, including by the OS. Returns an unsubscribe. */
  onMaximizedChanged: (handler) => {
    const listener = (_event, isMaximized) => handler(isMaximized);
    ipcRenderer.on('window:maximized-changed', listener);
    return () => ipcRenderer.removeListener('window:maximized-changed', listener);
  },

  /** Applies the saved background settings: close to the tray, and start at sign-in. */
  setBackgroundOptions: ({ keepRunningInBackground, openAtLogin }) =>
    ipcRenderer.invoke('app:setBackgroundOptions', { keepRunningInBackground, openAtLogin }),

  /** 'system' | 'light' | 'dark': keeps native dialogs and the window background in step with the page. */
  setTheme: (theme) => ipcRenderer.invoke('app:setTheme', theme),
});
