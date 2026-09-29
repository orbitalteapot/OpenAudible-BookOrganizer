/**
 * The three paths a sort needs, as the settings name them (`field`), as `pathStatus` names them
 * (`status`), and as the server's environment names them (`variable`) — Docker users set them there.
 * `noun` is the name inside a sentence; `description` says what the path is for, for someone setting
 * it up for the first time, and that the source is only ever read.
 */
export const PATH_FIELDS = [
  {
    field: 'csvPath',
    status: 'csv',
    variable: 'CSV_PATH',
    label: 'OpenAudible CSV export',
    noun: 'CSV export',
    kind: 'file',
    description: 'Made in OpenAudible with File > Export.',
  },
  {
    field: 'sourcePath',
    status: 'source',
    variable: 'SOURCE_PATH',
    label: 'Source folder',
    noun: 'source folder',
    kind: 'folder',
    description: 'Where OpenAudible keeps your downloaded books. Files here are only read, never changed.',
  },
  {
    field: 'destinationPath',
    status: 'destination',
    variable: 'DESTINATION_PATH',
    label: 'Destination folder',
    noun: 'destination folder',
    kind: 'folder',
    description: 'A separate folder where the organized copy is kept, such as your Audiobookshelf library.',
  },
];

export const CSV_FIELD = PATH_FIELDS[0];

/** "/data/books.csv" → "/data"; "/books.csv" → "/". */
function folderOf(path) {
  return path.replace(/[\\/][^\\/]*$/, '') || path.charAt(0);
}

/**
 * One path's status, as `{ tone, text }`: the last error the backend gave for it, otherwise what the
 * backend sees there now. In the browser the paths come from the container, so a path that is not
 * found is explained in terms of the container's volumes and variables, which is where it is fixed.
 * The one wording for this, used by the Folders card and by the Library, where the app opens.
 */
export function describePath({ field, status, kind, variable }, settings, error, isElectron) {
  if (error) return { tone: 'critical', text: error };

  switch (settings?.pathStatus?.[status]) {
    case 'ok':
      return { tone: 'positive', text: 'Found' };
    case 'notFound': {
      const what = kind === 'file' ? 'File' : 'Folder';
      if (isElectron) return { tone: 'critical', text: `${what} not found` };
      return {
        tone: 'critical',
        text:
          kind === 'file'
            ? `${what} not found inside the container — check that the folder holding it is mapped to ${folderOf(settings[field])} and that ${variable} names the file`
            : `${what} not found inside the container — check the volume mapping for ${variable}, or create the folder on the host if the mapping is right`,
      };
    }
    case 'notWritable':
      return { tone: 'critical', text: "Can't write to this folder" };
    default:
      return { tone: 'neutral', text: isElectron ? 'Not set' : `Not set — set ${variable} in the container` };
  }
}

/** Error codes that say a path is not there, which the backend finding it later answers. */
const NOT_THERE_CODES = new Set(['notSet', 'notFound', 'destinationMissing', 'csvNotFound']);

/**
 * Whether an error about a path (`{ field, code }`, as the backend sent it) is moot because the
 * backend now finds that path: a drive was plugged in, a volume mounted. The one rule for every
 * path error, however it came about (a refused save, a refused start). Only errors that said the path
 * was not there: "can't write" or "inside the source" are not answered by the folder being found.
 */
export function isFoundAgain({ field, code }, pathStatus) {
  const path = PATH_FIELDS.find((candidate) => candidate.field === field);
  return Boolean(path) && NOT_THERE_CODES.has(code) && pathStatus?.[path.status] === 'ok';
}

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
