/**
 * The three paths a sort needs, as the settings name them (`field`), as `pathStatus` names them
 * (`status`), and as the server's environment names them (`variable`) — Docker users set them there.
 * `noun` is the name inside a sentence.
 */
export const PATH_FIELDS = [
  { field: 'csvPath', status: 'csv', variable: 'CSV_PATH', label: 'OpenAudible CSV export', noun: 'CSV export', kind: 'file' },
  { field: 'sourcePath', status: 'source', variable: 'SOURCE_PATH', label: 'Source folder', noun: 'source folder', kind: 'folder' },
  { field: 'destinationPath', status: 'destination', variable: 'DESTINATION_PATH', label: 'Destination folder', noun: 'destination folder', kind: 'folder' },
];

/** "a", "a and b", "a, b and c". */
function listOf(items) {
  return items.length < 2 ? items.join('') : `${items.slice(0, -1).join(', ')} and ${items[items.length - 1]}`;
}

/**
 * Why a sort cannot start with these settings because a path is missing, or null when all three
 * are set. The one check behind Start sorting and turning on automatic sorting. Whether the paths
 * work is the backend's to say when asked; this only catches what can be seen without asking.
 */
export function pathsBlockedReason(settings, isElectron) {
  const missing = PATH_FIELDS.filter(({ field }) => !settings?.[field]);
  if (missing.length === 0) return null;

  return isElectron && !settings?.locks?.paths
    ? `Choose the ${listOf(missing.map(({ noun }) => noun))} first.`
    : `Set ${listOf(missing.map(({ variable }) => variable))} in the container.`;
}
