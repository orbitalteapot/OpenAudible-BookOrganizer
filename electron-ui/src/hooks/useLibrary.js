import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { parseLibrary } from '../api';

const EMPTY = { books: [], skippedRows: 0, warnings: [] };

/**
 * The books in the library export named in the settings. Read again whenever `csvPath` changes —
 * on launch once the settings arrive, and after a different export is chosen on either page — so
 * the Library and the Sort page always work from the same file.
 *
 * `loaded` is set once an export has been read, to tell "nothing read yet" from "read, but empty".
 */
export default function useLibrary(csvPath) {
  const [library, setLibrary] = useState(EMPTY);
  const [loaded, setLoaded] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const latest = useRef(0);

  const reload = useCallback(async () => {
    // Choosing a second export while the first is still being read must not end on the first.
    const id = ++latest.current;
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
    } catch (err) {
      if (id === latest.current) setError(err.message);
    } finally {
      if (id === latest.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    if (csvPath) reload();
  }, [csvPath, reload]);

  return useMemo(() => ({ ...library, loaded, loading, error, reload }), [library, loaded, loading, error, reload]);
}
