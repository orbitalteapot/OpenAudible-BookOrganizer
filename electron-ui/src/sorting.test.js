import { describe, expect, it } from 'vitest';
import { compareBooks, filterBooks, sortKey } from './sorting';
import { durationMinutes, formatDuration } from './format';

const book = (overrides) => ({ title: 'A', author: 'A', ...overrides });
const order = (books, field, dir = 'asc') => [...books].sort(compareBooks(field, dir));

describe('durationMinutes', () => {
  it('reads hours and minutes', () => {
    expect(durationMinutes('12 hrs and 34 mins')).toBe(754);
    expect(durationMinutes('1 hr and 1 min')).toBe(61);
  });

  it('reads a value with only one part', () => {
    expect(durationMinutes('45 mins')).toBe(45);
    expect(durationMinutes('7 hrs')).toBe(420);
  });

  it('returns null for anything it does not understand', () => {
    expect(durationMinutes('')).toBeNull();
    expect(durationMinutes(null)).toBeNull();
    expect(durationMinutes('unknown')).toBeNull();
  });
});

describe('formatDuration', () => {
  it('abbreviates, because the column truncates the prose form to nothing useful', () => {
    expect(formatDuration('12 hrs and 34 mins')).toBe('12h 34m');
    expect(formatDuration('45 mins')).toBe('45m');
    expect(formatDuration('7 hrs')).toBe('7h');
  });

  it('passes through a value it cannot parse rather than dropping it', () => {
    expect(formatDuration('unknown')).toBe('unknown');
  });

  it('shows an em dash when there is nothing to show', () => {
    expect(formatDuration('')).toBe('—');
    expect(formatDuration(undefined)).toBe('—');
  });
});

describe('sorting by duration', () => {
  // Regression: sorting compared the raw text, so "45 mins" was read as 45 against the 9 of
  // "9 hrs and 9 mins" and a three-quarter-hour book was filed after a nine-hour one.
  it('orders by length, not by the leading number', () => {
    const books = [
      book({ title: 'nine hours', duration: '9 hrs and 9 mins' }),
      book({ title: 'forty five minutes', duration: '45 mins' }),
      book({ title: 'one hour', duration: '1 hr and 1 min' }),
    ];

    expect(order(books, 'duration').map((b) => b.title)).toEqual([
      'forty five minutes',
      'one hour',
      'nine hours',
    ]);
  });

  it('sinks books with an unreadable duration, in both directions', () => {
    const books = [
      book({ title: 'unreadable', duration: 'unknown' }),
      book({ title: 'short', duration: '10 mins' }),
      book({ title: 'long', duration: '10 hrs' }),
    ];

    expect(order(books, 'duration', 'asc').map((b) => b.title)).toEqual(['short', 'long', 'unreadable']);
    expect(order(books, 'duration', 'desc').map((b) => b.title)).toEqual(['long', 'short', 'unreadable']);
  });
});

describe('sorting by series', () => {
  // Regression: the key was seriesName alone, so every book in a series tied and kept whatever
  // order the export used — routinely #10 above #2.
  it('orders books within a series by their sequence', () => {
    const books = [
      book({ title: 'tenth', seriesName: 'Numbers', seriesSequence: '10' }),
      book({ title: 'second', seriesName: 'Numbers', seriesSequence: '2' }),
      book({ title: 'first', seriesName: 'Numbers', seriesSequence: '1' }),
    ];

    expect(order(books, 'seriesName').map((b) => b.title)).toEqual(['first', 'second', 'tenth']);
  });

  it('handles a fractional sequence', () => {
    const books = [
      book({ title: 'two', seriesName: 'S', seriesSequence: '2' }),
      book({ title: 'one and a half', seriesName: 'S', seriesSequence: '1.5' }),
      book({ title: 'one', seriesName: 'S', seriesSequence: '1' }),
    ];

    expect(order(books, 'seriesName').map((b) => b.title)).toEqual(['one', 'one and a half', 'two']);
  });

  it('groups by series name before sequence', () => {
    const books = [
      book({ title: 'b1', seriesName: 'Bravo', seriesSequence: '1' }),
      book({ title: 'a2', seriesName: 'Alpha', seriesSequence: '2' }),
      book({ title: 'a1', seriesName: 'Alpha', seriesSequence: '1' }),
    ];

    expect(order(books, 'seriesName').map((b) => b.title)).toEqual(['a1', 'a2', 'b1']);
  });

  it('sinks standalone books', () => {
    const books = [
      book({ title: 'standalone' }),
      book({ title: 'in a series', seriesName: 'S', seriesSequence: '1' }),
    ];

    expect(order(books, 'seriesName').map((b) => b.title)).toEqual(['in a series', 'standalone']);
  });
});

describe('sorting by title and rating', () => {
  it('orders titles naturally, so 2 comes before 10', () => {
    const books = [book({ title: 'Book 10' }), book({ title: 'Book 2' }), book({ title: 'Book 1' })];
    expect(order(books, 'title').map((b) => b.title)).toEqual(['Book 1', 'Book 2', 'Book 10']);
  });

  it('ignores case and accents when ordering', () => {
    const books = [book({ title: 'Zebra' }), book({ title: 'Ápple' }), book({ title: 'banana' })];
    expect(order(books, 'title').map((b) => b.title)).toEqual(['Ápple', 'banana', 'Zebra']);
  });

  it('compares ratings as numbers and sinks unrated books', () => {
    const books = [
      book({ title: 'unrated', aveRating: 0 }),
      book({ title: 'good', aveRating: 4.5 }),
      book({ title: 'better', aveRating: 4.9 }),
    ];

    expect(order(books, 'aveRating', 'desc').map((b) => b.title)).toEqual(['better', 'good', 'unrated']);
  });
});

describe('sortKey', () => {
  it('falls back to the named field for a plain column', () => {
    expect(sortKey(book({ narratedBy: 'Simon Vance' }), 'narratedBy')).toBe('Simon Vance');
  });
});

describe('filterBooks', () => {
  const library = [
    book({ title: 'The Hobbit', author: 'Tolkien', narratedBy: 'Rob Inglis', seriesName: 'Middle-earth' }),
    book({ title: 'Dune', author: 'Herbert', narratedBy: 'Simon Vance' }),
  ];

  it('returns everything for a blank query', () => {
    expect(filterBooks(library, '')).toHaveLength(2);
    expect(filterBooks(library, '   ')).toHaveLength(2);
  });

  it('matches title, author, narrator and series', () => {
    expect(filterBooks(library, 'hobbit')).toHaveLength(1);
    expect(filterBooks(library, 'herbert')).toHaveLength(1);
    expect(filterBooks(library, 'vance')).toHaveLength(1);
    expect(filterBooks(library, 'middle')).toHaveLength(1);
  });

  it('ignores case and surrounding whitespace', () => {
    expect(filterBooks(library, '  DUNE ')).toHaveLength(1);
  });

  it('tolerates books missing the fields it searches', () => {
    expect(() => filterBooks([{ title: 'Only a title' }], 'title')).not.toThrow();
  });
});
