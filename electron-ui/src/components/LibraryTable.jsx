import { memo } from 'react';
import { ArrowDown, ArrowUp, Search } from 'lucide-react';
import { useElementWidth, useVirtualRows } from '../hooks';
import { formatDuration } from '../format';

const ROW_HEIGHT = 40;

/**
 * Column definitions, in priority order. `minTableWidth` is the width below which the column is
 * dropped, so a narrow window sheds detail instead of crushing every column into ellipses.
 */
const COLUMNS = [
  { key: 'title', label: 'Title', width: '2.4fr', minTableWidth: 0 },
  { key: 'author', label: 'Author', width: '1.5fr', minTableWidth: 0 },
  { key: 'seriesName', label: 'Series', width: '1.4fr', minTableWidth: 720 },
  { key: 'narratedBy', label: 'Narrator', width: '1.4fr', minTableWidth: 940 },
  { key: 'duration', label: 'Duration', width: '92px', minTableWidth: 560, align: 'right' },
  { key: 'aveRating', label: 'Rating', width: '80px', minTableWidth: 1120, align: 'right' },
];

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

const Row = memo(function Row({ book, columns }) {
  return (
    <tr className="border-b border-line/60 hover:bg-raised/50">
      {columns.map((column) => {
        const value = cellValue(book, column.key);
        const isTitle = column.key === 'title';

        return (
          <td
            key={column.key}
            title={cellTitle(book, column.key) ?? (value !== '—' ? value : undefined)}
            className={[
              'truncate px-4',
              column.align === 'right' ? 'text-right tabular' : '',
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
 */
export default function LibraryTable({ books, sortField, sortDir, onSort, searchActive }) {
  const [containerRef, containerWidth] = useElementWidth();
  const columns = COLUMNS.filter((column) => containerWidth >= column.minTableWidth);

  const { scrollRef, start, end, paddingTop, paddingBottom } = useVirtualRows({
    count: books.length,
    rowHeight: ROW_HEIGHT,
  });

  const visible = books.slice(start, end);

  return (
    <div ref={containerRef} className="flex min-h-0 flex-1 flex-col overflow-hidden rounded-lg border border-line bg-surface">
      <div ref={scrollRef} className="min-h-0 flex-1 overflow-auto">
        <table className="w-full table-fixed border-collapse text-sm">
          <colgroup>
            {columns.map((column) => (
              <col key={column.key} style={{ width: column.width }} />
            ))}
          </colgroup>

          <thead className="sticky top-0 z-10">
            <tr>
              {columns.map((column) => {
                const active = sortField === column.key;

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
                      className={[
                        'flex h-10 w-full items-center gap-1.5 px-4 text-xs transition-colors',
                        column.align === 'right' ? 'justify-end' : '',
                        active ? 'text-fg' : 'text-fg-muted hover:text-fg',
                      ].join(' ')}
                    >
                      {column.label}
                      <SortIndicator active={active} direction={sortDir} />
                    </button>
                  </th>
                );
              })}
            </tr>
          </thead>

          <tbody>
            {paddingTop > 0 && (
              <tr aria-hidden="true">
                <td colSpan={columns.length} style={{ height: paddingTop }} />
              </tr>
            )}

            {visible.map((book, index) => (
              <Row key={book.key || book.asin || start + index} book={book} columns={columns} />
            ))}

            {paddingBottom > 0 && (
              <tr aria-hidden="true">
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
