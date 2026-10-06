import { describe, expect, it } from 'vitest';
import { formatDateTime, formatInterval, formatRelative, summariseRun } from './format';

describe('formatInterval', () => {
  it.each([
    [null, 'Off'],
    [60, 'Every hour'],
    [360, 'Every 6 hours'],
    [1440, 'Every day'],
    [10080, 'Every 7 days'],
    [45, 'Every 45 minutes'],
  ])('describes %s minutes as "%s"', (minutes, expected) => {
    expect(formatInterval(minutes)).toBe(expected);
  });
});

const counts = (overrides = {}) => ({ new: 0, updated: 0, moved: 0, upToDate: 0, notFound: 0, failed: 0, ...overrides });

describe('summariseRun', () => {
  it('lists only what happened, in the tile order', () => {
    const summary = summariseRun({ counts: counts({ upToDate: 120, new: 3 }), problemCount: 0 });

    expect(summary.headline).toBe('Sort complete: 3 new, 120 up to date.');
    expect(summary.details).toEqual([]);
    expect(summary.tone).toBe('positive');
  });

  it('explains moved and missing books, and flags them', () => {
    const summary = summariseRun({ counts: counts({ moved: 2, notFound: 1, upToDate: 5 }), problemCount: 1 });

    expect(summary.headline).toBe('Sort complete: 2 moved, 5 up to date, 1 not found.');
    expect(summary.details).toEqual([
      '2 books already in the destination folder were moved to where they now belong: left by an older version (loose, or sharing a Book N folder), or filed before their author, series, number or title changed.',
      '1 book in the export has no file in the source folder. These are usually books that have not been downloaded yet.',
    ]);
    expect(summary.tone).toBe('caution');
  });

  it('treats a failed book as critical', () => {
    const summary = summariseRun({ counts: counts({ failed: 1, new: 4 }), problemCount: 1 });

    expect(summary.tone).toBe('critical');
    expect(summary.details).toContain('1 book could not be copied. The problems list says why.');
  });

  it('says how far a canceled run got', () => {
    const summary = summariseRun({ counts: counts({ new: 1, upToDate: 2 }), isCanceled: true });

    expect(summary.headline).toBe('Sort canceled after 3 books.');
    expect(summary.details[0]).toBe('Before it stopped: 1 new, 2 up to date.');
    expect(summary.tone).toBe('caution');
  });

  it('uses the reason the backend gave for a cancel nobody asked for', () => {
    const summary = summariseRun({ counts: counts(), isCanceled: true, error: 'Canceled because the app closed.' });

    expect(summary.headline).toBe('Canceled because the app closed.');
  });

  it('reports a failure with its reason', () => {
    const summary = summariseRun({
      counts: counts(),
      error: 'The destination folder does not exist. Is the drive connected?',
      errorCode: 'destinationMissing',
    });

    expect(summary.headline).toBe('Sort failed: The destination folder does not exist. Is the drive connected?');
    expect(summary.details).toEqual([]);
    expect(summary.tone).toBe('critical');
  });

  it('points at the source folder, not at downloads, when no book was found at all', () => {
    const summary = summariseRun({ counts: counts({ notFound: 1204 }), problemCount: 1204 });

    expect(summary.headline).toBe('Sort complete, but no book was found in the source folder (1,204 books in the export).');
    expect(summary.details).toEqual([
      'None of the books in the export were found in the source folder. Check that the source folder is the one OpenAudible downloads your books into, and that SOURCE_PATH is mapped to it.',
    ]);
    expect(summary.tone).toBe('critical');
  });

  it('describes an export with no books', () => {
    expect(summariseRun({ counts: counts() }).headline).toBe('Sort complete: the export lists no books.');
  });
});

describe('formatDateTime', () => {
  const now = new Date(2026, 8, 29, 18, 0);

  it('names today, yesterday and tomorrow', () => {
    expect(formatDateTime(new Date(2026, 8, 29, 21, 14), now)).toMatch(/^today at /);
    expect(formatDateTime(new Date(2026, 8, 28, 21, 14), now)).toMatch(/^yesterday at /);
    expect(formatDateTime(new Date(2026, 8, 30, 9, 0), now)).toMatch(/^tomorrow at /);
  });

  it('gives a date further away', () => {
    expect(formatDateTime(new Date(2026, 9, 5, 9, 0), now)).not.toMatch(/^(today|yesterday|tomorrow)/);
  });
});

describe('formatRelative', () => {
  const now = new Date(2026, 8, 29, 18, 0);
  const later = (minutes) => new Date(now.getTime() + minutes * 60_000);

  it('rounds to the nearest sensible unit', () => {
    expect(formatRelative(later(180), now)).toBe('in 3 hours');
    expect(formatRelative(later(-5), now)).toBe('5 minutes ago');
    expect(formatRelative(later(1440), now)).toBe('in 24 hours');
    expect(formatRelative(later(2 * 1440), now)).toBe('in 2 days');
    expect(formatRelative(now, now)).toBe('now');
  });
});
