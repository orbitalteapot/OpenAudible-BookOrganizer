import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { parseLibrary } from '../api';

const EMPTY = { books: [], skippedRows: 0, warnings: [] };

/**
 * The books in the library export named in the settings. Read again whenever `csvPath` changes —
 * on launch once the settings arrive, and after a different export is chosen on either page — so
 * the Library and the Sort page always work from the same file.
 *
 * `loaded` is set once the current export has been read, to tell "nothing read yet" from "read,
 * but empty".
 *
 * `csvFound` is whether the backend last saw the export on disk. A read that failed is tried again
 * when it turns true, so an export on a drive plugged in, or a volume mounted, after the app started
 * loads without the user having to find Reload.
 *
 * `refreshStatus` asks the backend for the path statuses again. It is called when a read succeeds
 * while `csvFound` still says the export is missing, so the Library does not show "File not found"
 * above the books it just read until the next timed refresh.
 */
export default function useLibrary(csvPath, csvFound, refreshStatus) {
  const [library, setLibrary] = useState(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const latest = useRef(0);
  // Read in the effect below, which must run only when the export turns up, not after every failure.
  const lastReadFailed = useRef(false);
  // Read by `reload`, which stays the same function so the effects below do not re-run on every render.
  const statusRef = useRef({ csvFound, refreshStatus });
  statusRef.current = { csvFound, refreshStatus };

  const reload = useCallback(async () => {
    // Choosing a second export while the first is still being read must not end on the first.
    const id = ++latest.current;
    lastReadFailed.current = false;
    setLoading(true);
    setError(null);

    try {
      const result = await parseLibrary();
      if (id !== latest.current) return;
      setLibrary({
        books: result.books ?? [],
        skippedRows: result.skippedRows ?? 0,
        warnings: result.warnings ?? [],
      });
      setLoaded(true);
      const status = statusRef.current;
      if (!status.csvFound) status.refreshStatus?.();
    } catch (err) {
      if (id === latest.current) {
        lastReadFailed.current = true;
        setError(err.message);
      }
    } finally {
      if (id === latest.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    // Another export's books must never be shown under this path, even if reading it fails.
    // Reload of the same path keeps the current list, so a failed re-read leaves it on screen.
    setLibrary(EMPTY);
    setLoaded(false);
    if (csvPath) reload();
  }, [csvPath, reload]);

  useEffect(() => {
    if (csvFound && lastReadFailed.current) reload();
  }, [csvFound, reload]);

  return useMemo(() => ({ ...library, loaded, loading, error, reload }), [library, loaded, loading, error, reload]);
}
