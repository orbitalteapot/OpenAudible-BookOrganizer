// Native pickers, for the desktop app only: the browser build has no way to choose server paths.

const CSV_FILTERS = [{ name: 'CSV Files', extensions: ['csv'] }];

/**
 * Asks for one of the paths in PATH_FIELDS (the export, the source or the destination folder), in a
 * dialog that says which one it is for and opens where `currentPath` is, so fixing one level of a long
 * network path does not mean finding it again from the home folder. Resolves to the chosen path, or
 * null when the user cancels.
 */
export async function choosePath({ noun, kind }, currentPath) {
  const options = {
    title: `Choose the ${noun}`,
    message: `Choose the ${noun}`,
    buttonLabel: kind === 'file' ? 'Use this file' : 'Use this folder',
    defaultPath: currentPath || undefined,
  };
  const api = window.electronAPI;
  const chosen = kind === 'file' ? await api?.openFile({ ...options, filters: CSV_FILTERS }) : await api?.openFolder(options);
  return chosen || null;
}
