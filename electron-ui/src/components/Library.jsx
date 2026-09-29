import { memo, useCallback, useMemo, useRef, useState } from 'react';
import { BookOpen, FileUp, RefreshCw, Search } from 'lucide-react';
import { choosePath } from '../desktop';
import { useDebounced, useFocusFallback } from '../hooks';
import { CSV_FIELD, describePath } from '../paths';
import { compareBooks, filterBooks, SORT_LABELS } from '../sorting';
import Button from './ui/Button';
import Disclosure from './ui/Disclosure';
import { TextInput } from './ui/Field';
import { Banner, EmptyState } from './ui/Surface';
import LibraryTable from './LibraryTable';

/** Rows the export had that could not be read, with the reasons, folded away until asked for. */
function ImportNotice({ skippedRows, warnings }) {
  if (skippedRows === 0 && warnings.length === 0) return null;

  const summary =
    skippedRows > 0
      ? `${skippedRows.toLocaleString()} row${skippedRows === 1 ? '' : 's'} of the export could not be read and ${skippedRows === 1 ? 'was' : 'were'} skipped`
      : `${warnings.length.toLocaleString()} warning${warnings.length === 1 ? '' : 's'} while reading the export`;

  return (
    <Disclosure summary={summary}>
      <ul className="max-h-40 space-y-1 overflow-y-auto rounded border border-line bg-raised px-3 py-2 text-2xs text-fg-muted">
        {warnings.map((warning, index) => (
          <li key={index} className="break-words">
            {warning}
          </li>
        ))}
      </ul>
    </Disclosure>
  );
}

/**
 * Reload, and on the desktop "Choose export…". The browser build reads the export the server names.
 * `buttonProps` go on both buttons.
 */
function LibraryActions({ library, isElectron, onChoose, csvPath, buttonProps }) {
  return (
    <>
      {csvPath && (
        <Button icon={RefreshCw} loading={library.loading} onClick={library.reload} {...buttonProps}>
          Reload
        </Button>
      )}
      {isElectron && (
        <Button variant={csvPath ? 'secondary' : 'primary'} icon={FileUp} onClick={onChoose} {...buttonProps}>
          Choose export…
        </Button>
      )}
    </>
  );
}

/** What to show before there are books: why there are none, and what to do about it. */
function emptyStateText({ library, csvPath, isElectron }) {
  if (!csvPath) {
    return {
      title: 'No library export chosen',
      description: isElectron
        ? 'Export your library from OpenAudible as a CSV file, then choose it here to browse, search and sort your books.'
        : 'Set CSV_PATH in the container to your OpenAudible CSV export to browse your books here.',
    };
  }
  if (library.loading) return { title: 'Reading your library…', description: csvPath };
  if (library.error) return { title: "Couldn't read the library export", description: csvPath };
  if (library.loaded) return { title: 'The export has no books', description: csvPath };
  return { title: 'No audiobooks loaded', description: csvPath };
}

/**
 * The books in the export chosen in the settings — the same file the Sort page sorts from. It loads
 * on its own once the settings arrive; Reload reads it again, and on the desktop "Choose export…"
 * picks a different one, which is then saved for both pages.
 */
function LibraryView({ library, settings, update, fieldErrors, isElectron }) {
  const [search, setSearch] = useState('');
  const [sort, setSort] = useState({ field: 'title', dir: 'asc' });
  // The last "Choose export…" refusal, when that is not about the export itself: saving a new export
  // checks every folder, and an unplugged destination or a settings file that cannot be written would
  // otherwise leave the click looking as if it did nothing.
  const [refusal, setRefusal] = useState(null);
  const debouncedSearch = useDebounced(search);

  const { books } = library;
  const csvPath = settings.csvPath;
  // An export the backend cannot see is explained the way the Folders card does, which in the browser
  // names the container's mapping and CSV_PATH; any other failure to read it is the backend's own words.
  const csvStatus = describePath(CSV_FIELD, settings, fieldErrors.csvPath, isElectron);
  // A refusal about a folder is shown for as long as `fieldErrors` holds it, so it goes once the drive
  // is plugged back in, as it does on the Folders card, and stops hiding what is wrong with the export.
  const refusalText = refusal?.field ? fieldErrors[refusal.field] : refusal?.message;
  const error = refusalText
    ? `Couldn't use this export: ${refusalText}`
    : csvStatus.tone === 'critical'
      ? csvStatus.text
      : library.error;

  // The empty page's buttons go when the books arrive; the search field is where the table starts.
  const searchRef = useRef(null);
  const emptyActionFocus = useFocusFallback(books.length > 0, searchRef);

  const handleChoose = useCallback(async () => {
    const path = await choosePath(CSV_FIELD, csvPath);
    if (!path) return;
    setRefusal(null);
    // A different export is loaded as soon as the saved path changes; the same one is just re-read.
    if (path === csvPath) {
      library.reload();
      return;
    }
    const refused = await update({ csvPath: path });
    // One about the export itself is already shown, as the Folders card shows it.
    if (refused && refused.field !== CSV_FIELD.field) setRefusal(refused);
  }, [csvPath, library, update]);

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
    const { title, description } = emptyStateText({ library, csvPath, isElectron });

    return (
      <EmptyState icon={BookOpen} title={title} description={description}>
        <div className="flex flex-wrap justify-center gap-2">
          <LibraryActions
            library={library}
            isElectron={isElectron}
            onChoose={handleChoose}
            csvPath={csvPath}
            buttonProps={emptyActionFocus}
          />
        </div>
        {error && <Banner tone="critical">{error}</Banner>}
        <ImportNotice skippedRows={library.skippedRows} warnings={library.warnings} />
      </EmptyState>
    );
  }

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold tracking-tight text-fg">Library</h1>
          <p className="tabular text-xs text-fg-muted">
            {rows.length === books.length
              ? `${books.length.toLocaleString()} audiobooks`
              : `${rows.length.toLocaleString()} of ${books.length.toLocaleString()} audiobooks`}
          </p>
          <p className="max-w-md truncate text-2xs text-fg-subtle" title={csvPath}>
            {csvPath}
          </p>
        </div>

        <div className="flex items-center gap-2">
          <TextInput
            ref={searchRef}
            type="search"
            icon={Search}
            className="w-56"
            placeholder="Search"
            aria-label="Search books"
            value={search}
            onChange={(event) => setSearch(event.target.value)}
          />
          <LibraryActions library={library} isElectron={isElectron} onChoose={handleChoose} csvPath={csvPath} />
        </div>
      </div>

      {error && <Banner tone="critical">{error}</Banner>}
      <ImportNotice skippedRows={library.skippedRows} warnings={library.warnings} />

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
