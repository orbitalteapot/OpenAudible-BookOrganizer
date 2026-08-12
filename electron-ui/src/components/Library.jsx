import { memo, useCallback, useMemo, useState } from 'react';
import { BookOpen, FileUp, Search } from 'lucide-react';
import { parseBooks } from '../api';
import { useDebounced, useIsElectron } from '../hooks';
import { compareBooks, filterBooks, SORT_LABELS } from '../sorting';
import Button from './ui/Button';
import { TextInput } from './ui/Field';
import { Banner, EmptyState } from './ui/Surface';
import LibraryTable from './LibraryTable';

function LibraryView({ books, setBooks, csvPath }) {
  const isElectron = useIsElectron();
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState({ field: 'title', dir: 'asc' });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const [notice, setNotice] = useState(null);

  const debouncedSearch = useDebounced(search);

  const handleLoadCsv = useCallback(async () => {
    let filePath = csvPath;

    if (isElectron) {
      filePath = await window.electronAPI?.openFile([{ name: 'CSV Files', extensions: ['csv'] }]);
      if (!filePath) return;
    } else if (!filePath) {
      setError('No CSV path is configured for web mode');
      return;
    }

    setLoading(true);
    setError(null);
    setNotice(null);

    try {
      const { books: loaded, skippedRows } = await parseBooks(filePath);
      setBooks(loaded);

      if (skippedRows > 0) {
        setNotice(
          `${skippedRows} row${skippedRows === 1 ? '' : 's'} could not be read and were skipped. ` +
            `${loaded.length} book${loaded.length === 1 ? '' : 's'} loaded.`
        );
      } else if (loaded.length === 0) {
        setNotice('The export was read successfully but contained no books.');
      }
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  }, [csvPath, isElectron, setBooks]);

  const handleSort = useCallback((field) => {
    setSort((prev) => (prev.field === field ? { field, dir: prev.dir === 'asc' ? 'desc' : 'asc' } : { field, dir: 'asc' }));
  }, []);

  // The window got too narrow for the column being sorted on. Fall back to Title, which is never
  // dropped, so the order on screen always corresponds to a header the user can see and change.
  const handleSortFieldHidden = useCallback(() => {
    setSort({ field: 'title', dir: 'asc' });
  }, []);

  // Filtering and sorting are separate passes so that typing does not pay for a re-sort and
  // re-sorting does not pay for a re-filter.
  const filtered = useMemo(() => filterBooks(books, debouncedSearch), [books, debouncedSearch]);

  const rows = useMemo(
    () => [...filtered].sort(compareBooks(sort.field, sort.dir)),
    [filtered, sort]
  );

  const sortLabel = SORT_LABELS[sort.field] ?? sort.field;
  const announcement = [
    debouncedSearch.trim()
      ? rows.length === 0
        ? `No books match ${debouncedSearch.trim()}`
        : `${rows.length.toLocaleString()} of ${books.length.toLocaleString()} books match ${debouncedSearch.trim()}`
      : `${books.length.toLocaleString()} books`,
    `sorted by ${sortLabel}, ${sort.dir === 'asc' ? 'ascending' : 'descending'}`,
  ].join(', ');

  if (books.length === 0) {
    return (
      <EmptyState
        icon={BookOpen}
        title="No audiobooks loaded"
        description={
          isElectron
            ? 'Import your OpenAudible CSV export to browse, search and sort your collection.'
            : 'Load the CSV export configured on the server to browse your collection.'
        }
      >
        <Button variant="primary" icon={FileUp} loading={loading} onClick={handleLoadCsv}>
          {isElectron ? 'Load CSV export' : 'Load library'}
        </Button>
        {!isElectron && csvPath && (
          <p className="max-w-sm break-all text-2xs text-fg-subtle">{csvPath}</p>
        )}
        {error && <Banner tone="critical">{error}</Banner>}
        {notice && <Banner tone="caution">{notice}</Banner>}
      </EmptyState>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-lg font-semibold text-fg">Library</h1>
          <p className="tabular text-xs text-fg-muted">
            {rows.length === books.length
              ? `${books.length.toLocaleString()} audiobooks`
              : `${rows.length.toLocaleString()} of ${books.length.toLocaleString()} audiobooks`}
          </p>
        </div>

        <div className="flex items-center gap-2">
          <TextInput
            type="search"
            icon={Search}
            className="w-56"
            placeholder="Search"
            aria-label="Search books"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <Button icon={FileUp} loading={loading} onClick={handleLoadCsv}>
            Reload
          </Button>
        </div>
      </div>

      {error && <Banner tone="critical">{error}</Banner>}
      {notice && <Banner tone="caution">{notice}</Banner>}

      {/*
        Always mounted, so it actually announces. A live region that appears at the same moment as
        its text is frequently missed: assistive tech has to be observing the node before the text
        lands in it.
      */}
      <p aria-live="polite" className="sr-only">
        {announcement}
      </p>

      <LibraryTable
        books={rows}
        sortField={sort.field}
        sortDir={sort.dir}
        onSort={handleSort}
        onSortFieldHidden={handleSortFieldHidden}
        searchActive={debouncedSearch.trim().length > 0}
        resetKey={`${debouncedSearch}\u0000${sort.field}\u0000${sort.dir}`}
      />
    </div>
  );
}

// Memoised so that polling a running sort — which updates state two and a half times a second in
// the shell above — does not re-render the whole book table alongside it.
export default memo(LibraryView);
