import { durationMinutes } from './format';

export const SEARCH_FIELDS = ['title', 'author', 'seriesName', 'narratedBy'];

export const SORT_LABELS = {
  title: 'title',
  author: 'author',
  seriesName: 'series',
  narratedBy: 'narrator',
  duration: 'duration',
  aveRating: 'rating',
};

// Natural ordering, so "Book 2" sorts before "Book 10" and accented names file where a reader
// expects. Built once rather than per comparison: constructing a collator is not cheap, and doing
// it inside the sort callback made large libraries noticeably slow to reorder.
const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

/**
 * What a column sorts on, which is not always the field it is named after.
 *
 * A column has to sort by what it shows. Sorting Series on `seriesName` alone left every book in a
 * series tied, so they kept whatever order the export happened to use — #10 above #2. Sorting
 * Duration on the raw text put a 45 minute book after a 9 hour one, because "45" reads as larger
 * than "9". Both are keyed on the meaning of the value rather than its spelling.
 */
const SORT_KEYS = {
  duration: (book) => durationMinutes(book.duration),
  aveRating: (book) => (book.aveRating > 0 ? book.aveRating : null),
  seriesName: (book) => (book.seriesName ? `${book.seriesName} #${book.seriesSequence ?? ''}` : null),
};

export function sortKey(book, field) {
  return SORT_KEYS[field] ? SORT_KEYS[field](book) : book[field];
}

const isBlank = (value) => value == null || value === '';

/** Comparator for one column and direction. Books missing the value always collect at the end. */
export function compareBooks(field, dir) {
  return (a, b) => {
    const left = sortKey(a, field);
    const right = sortKey(b, field);

    // Blanks sink regardless of direction, rather than forming a block of empty rows at the top
    // whenever the direction is flipped.
    if (isBlank(left)) return isBlank(right) ? 0 : 1;
    if (isBlank(right)) return -1;

    const comparison =
      typeof left === 'number' && typeof right === 'number'
        ? left - right
        : collator.compare(String(left), String(right));

    return dir === 'asc' ? comparison : -comparison;
  };
}

export function filterBooks(books, query) {
  const needle = query.trim().toLowerCase();
  if (!needle) return books;

  return books.filter((book) => SEARCH_FIELDS.some((field) => book[field]?.toLowerCase().includes(needle)));
}
