const { contextBridge, ipcRenderer } = require('electron');

// main.js picks the backend's port at launch and passes its URL as this argument.
const BACKEND_URL_ARG = '--backend-url=';
const backendUrl =
  process.argv.find((arg) => arg.startsWith(BACKEND_URL_ARG))?.slice(BACKEND_URL_ARG.length) ?? '';

contextBridge.exposeInMainWorld('electronAPI', {
  /** Where the backend listens, e.g. "http://127.0.0.1:49731". Empty if it could not be started. */
  backendUrl,

  openFile: (filters) => ipcRenderer.invoke('dialog:openFile', filters),
  openFolder: () => ipcRenderer.invoke('dialog:openFolder'),
  minimize: () => ipcRenderer.invoke('window:minimize'),
  maximize: () => ipcRenderer.invoke('window:maximize'),
  close: () => ipcRenderer.invoke('window:close'),
  isMaximized: () => ipcRenderer.invoke('window:isMaximized'),

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
