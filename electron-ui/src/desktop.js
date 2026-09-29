// Native pickers, for the desktop app only: the browser build has no way to choose server paths.

const CSV_FILTERS = [{ name: 'CSV Files', extensions: ['csv'] }];

/** Asks for an OpenAudible CSV export. Resolves to its path, or null when the user cancels. */
export async function chooseCsvFile() {
  return (await window.electronAPI?.openFile(CSV_FILTERS)) || null;
}

/** Asks for a folder. Resolves to its path, or null when the user cancels. */
export async function chooseFolder() {
  return (await window.electronAPI?.openFolder()) || null;
}
