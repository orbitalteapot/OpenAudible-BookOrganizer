import { memo, useEffect, useMemo } from 'react';
import { ArrowDown, ArrowUp, Search } from 'lucide-react';
import { useElementWidth, useVirtualRows } from '../hooks';
import { formatDuration } from '../format';

const ROW_HEIGHT = 40;

/**
 * Column definitions, in priority order. `minTableWidth` is the width below which the column is
 * dropped, so a narrow window sheds detail instead of crushing every column into ellipses. Title
 * and Author have no threshold: they are the floor, and below the width that fits them the table
 * scrolls sideways rather than shedding anything further.
 */
const COLUMNS = [
  { key: 'title', label: 'Title', width: '2.4fr', minTableWidth: 0 },
  { key: 'author', label: 'Author', width: '1.5fr', minTableWidth: 0 },
  { key: 'seriesName', label: 'Series', width: '1.4fr', minTableWidth: 720 },
  { key: 'narratedBy', label: 'Narrator', width: '1.4fr', minTableWidth: 940 },
  { key: 'duration', label: 'Duration', width: '92px', minTableWidth: 560, align: 'right' },
  { key: 'aveRating', label: 'Rating', width: '80px', minTableWidth: 1120, align: 'right' },
];

const MIN_TABLE_WIDTH = 420;

function cellValue(book, key) {
  if (key === 'seriesName') {
    if (!book.seriesName) return '—';
    return book.seriesSequence ? `${book.seriesName} #${book.seriesSequence}` : book.seriesName;
  }

  if (key === 'duration') {
    return formatDuration(book.duration);
  }

  if (key === 'aveRating') {
    return book.aveRating > 0 ? book.aveRating.toFixed(1) : '—';
  }

  return book[key] || '—';
}

/** The unabbreviated value, for the cell tooltip. */
function cellTitle(book, key) {
  if (key === 'duration') return book.duration || undefined;
  return undefined;
}

const Row = memo(function Row({ book, columns, rowIndex }) {
  return (
    <tr aria-rowindex={rowIndex} className="border-b border-line/60 hover:bg-raised/50">
      {columns.map((column) => {
        const value = cellValue(book, column.key);
        const isTitle = column.key === 'title';

        return (
          <td
            key={column.key}
            title={cellTitle(book, column.key) ?? (value !== '—' ? value : undefined)}
            className={[
              'truncate px-4',
              column.align === 'right' ? 'tabular text-right' : '',
              isTitle ? 'font-medium text-fg' : 'text-fg-muted',
            ].join(' ')}
            style={{ height: ROW_HEIGHT }}
          >
            {value}
          </td>
        );
      })}
    </tr>
  );
});

function SortIndicator({ active, direction }) {
  if (!active) return <span className="inline-block w-3.5" aria-hidden="true" />;
  const Icon = direction === 'asc' ? ArrowUp : ArrowDown;
  return <Icon size={13} className="shrink-0 text-accent" aria-hidden="true" />;
}

/**
 * The book list.
 *
 * A real <table> with <colgroup>: the column widths are declared once and shared by the header and
 * every row, so the two can no longer drift apart the way parallel flex widths did. Only the rows
 * near the viewport are rendered — see useVirtualRows — with spacer rows standing in for the rest,
 * which keeps a ten thousand book library scrolling at full speed.
 *
 * Because the DOM no longer holds a row per book, the row geometry has to be restated on the ARIA
 * layer: aria-rowcount on the table and aria-rowindex on each row, or assistive tech reports the
 * size of the window instead of the size of the library.
 */
export default function LibraryTable({
  books,
  sortField,
  sortDir,
  onSort,
  onSortFieldHidden,
  searchActive,
  resetKey,
}) {
  const [containerRef, containerWidth] = useElementWidth();

  // Keyed on the set of visible columns rather than the raw width: a new array on every pixel of
  // resize — or worse, on every render — would defeat the memo on Row and reconcile every visible
  // row on every scroll frame.
  const visibleKeys = COLUMNS.filter((column) => containerWidth >= column.minTableWidth)
    .map((column) => column.key)
    .join(',');

  const columns = useMemo(
    () => COLUMNS.filter((column) => visibleKeys.split(',').includes(column.key)),
    [visibleKeys]
  );

  const { scrollRef, start, end, paddingTop, paddingBottom, scrollToTop } = useVirtualRows({
    count: books.length,
    rowHeight: ROW_HEIGHT,
  });

  // A new result set starts at the top. Without this, searching from halfway down leaves scrollTop
  // pointing past the end of a much shorter list: the window resolves to an empty slice and the
  // user is shown a blank table that then judders upwards.
  useEffect(() => {
    scrollToTop();
  }, [resetKey, scrollToTop]);

  // Sorting by a column that is no longer rendered leaves the order unexplained and unchangeable.
  // Guarded on a measured width: before the first measurement every optional column looks absent,
  // and acting on that would silently discard the sort each time the view mounts.
  useEffect(() => {
    if (containerWidth > 0 && !columns.some((column) => column.key === sortField)) {
      onSortFieldHidden?.();
    }
  }, [containerWidth, columns, sortField, onSortFieldHidden]);

  const visible = books.slice(start, end);

  return (
    <div
      ref={containerRef}
      className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border border-line bg-surface"
    >
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto">
        <table
          aria-label="Audiobooks"
          aria-rowcount={books.length + 1}
          className="w-full table-fixed border-collapse text-sm"
          style={{ minWidth: MIN_TABLE_WIDTH }}
        >
          <colgroup>
            {columns.map((column) => (
              <col key={column.key} style={{ width: column.width }} />
            ))}
          </colgroup>

          <thead className="sticky top-0 z-10">
            <tr aria-rowindex={1}>
              {columns.map((column) => {
                const active = sortField === column.key;
                const nextDirection = active && sortDir === 'asc' ? 'descending' : 'ascending';

                return (
                  <th
                    key={column.key}
                    scope="col"
                    aria-sort={active ? (sortDir === 'asc' ? 'ascending' : 'descending') : 'none'}
                    className="border-b border-line bg-surface p-0 text-left font-medium"
                  >
                    <button
                      type="button"
                      onClick={() => onSort(column.key)}
                      // The visible label alone would leave a keyboard user with no idea what
                      // pressing it does, since the direction arrow is decorative.
                      aria-label={`${column.label}, sort ${nextDirection}`}
                      className={[
                        'flex h-10 w-full items-center gap-1.5 px-4 text-xs transition-colors',
                        column.align === 'right' ? 'justify-end' : '',
                        active ? 'text-fg' : 'text-fg-muted hover:text-fg',
                      ].join(' ')}
                    >
                      <span aria-hidden="true">{column.label}</span>
                      <SortIndicator active={active} direction={sortDir} />
                    </button>
                  </th>
                );
              })}
            </tr>
          </thead>

          <tbody>
            {/* role="presentation" so the spacers do not count themselves as rows. */}
            {paddingTop > 0 && (
              <tr role="presentation">
                <td colSpan={columns.length} style={{ height: paddingTop }} />
              </tr>
            )}

            {visible.map((book, index) => (
              <Row
                // Keyed by position in the list, not by anything from the book. A real export
                // repeats ASINs — a book bought twice, a re-issue kept beside the original — and
                // two rows sharing a key made React keep stale rows in the table instead of
                // replacing them: the list grew on every scroll and every re-sort, the scrollbar
                // described more content than existed, and row indices repeated. A development
                // build would have warned about it; the shipped build strips that warning.
                key={start + index}
                book={book}
                columns={columns}
                // +2: ARIA row indices are 1-based and the header occupies row 1.
                rowIndex={start + index + 2}
              />
            ))}

            {paddingBottom > 0 && (
              <tr role="presentation">
                <td colSpan={columns.length} style={{ height: paddingBottom }} />
              </tr>
            )}
          </tbody>
        </table>

        {books.length === 0 && searchActive && (
          <div className="flex flex-col items-center py-16 text-fg-subtle">
            <Search size={22} className="mb-3" aria-hidden="true" />
            <p className="text-sm">No books match your search</p>
          </div>
        )}
      </div>
    </div>
  );
}
