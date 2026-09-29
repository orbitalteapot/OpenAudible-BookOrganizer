import { isDesktop } from './mode';

// OpenAudible writes a book's length in one of two shapes depending on version and export
// settings: a clock value, "18:22:00", or prose, "12 hrs and 34 mins". Both are understood, and
// anything else is passed through untouched rather than being dropped.
const CLOCK_PATTERN = /^(\d+):([0-5]?\d)(?::([0-5]?\d))?$/;
const PROSE_PATTERN = /(?:(\d+)\s*h(?:ou)?rs?)?\D*(?:(\d+)\s*min(?:ute)?s?)?/i;

/**
 * Total length in minutes, or null when the value is not a duration we recognise.
 *
 * Sorting needs this rather than the text. Comparing "45 mins" against "9 hrs and 9 mins" as
 * strings — even with numeric collation — reads 45 against 9 and files three quarters of an hour
 * after nine hours. Clock values are worse: nothing in "18:22:00" tells a string comparison which
 * part is hours, and treating them as unparseable would sink every book in a real library to the
 * bottom of the column.
 */
export function durationMinutes(raw) {
  if (raw === null || raw === undefined) return null;

  const text = String(raw).trim();
  if (!text) return null;

  // "18:22:00" (h:mm:ss) or "18:22" (h:mm). Seconds are dropped rather than rounded, so the
  // displayed value never disagrees with the number the column is ordered by.
  const clock = CLOCK_PATTERN.exec(text);
  if (clock) {
    return Number(clock[1]) * 60 + Number(clock[2]);
  }

  const prose = PROSE_PATTERN.exec(text);
  if (!prose) return null;

  const hours = Number(prose[1] || 0);
  const minutes = Number(prose[2] || 0);
  if (!hours && !minutes) return null;

  return hours * 60 + minutes;
}

/**
 * At column width the source text truncates to "12 hrs and…", and a clock value gives no sense of
 * scale at a glance, so both are rewritten compactly. Anything unrecognised is shown as-is.
 */
export function formatDuration(raw) {
  if (raw === null || raw === undefined || String(raw).trim() === '') return '—';

  const text = String(raw).trim();
  const total = durationMinutes(text);
  if (total === null) return text;

  const hours = Math.floor(total / 60);
  const minutes = total % 60;

  if (!hours && !minutes) return '—';
  if (!hours) return `${minutes}m`;
  if (!minutes) return `${hours}h`;

  return `${hours}h ${minutes}m`;
}


/** "Every 6 hours", "Every day", "Every 45 minutes" — for a schedule interval in minutes. */
export function formatInterval(minutes) {
  if (!minutes) return 'Off';

  const [amount, unit] =
    minutes % 1440 === 0 ? [minutes / 1440, 'day'] : minutes % 60 === 0 ? [minutes / 60, 'hour'] : [minutes, 'minute'];

  return amount === 1 ? `Every ${unit}` : `Every ${amount} ${unit}s`;
}

const TIME_FORMAT = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const DATE_FORMAT = new Intl.DateTimeFormat(undefined, { weekday: 'short', day: 'numeric', month: 'short' });
const RELATIVE_FORMAT = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
const DAY_MS = 86_400_000;

/** Whole calendar days from `now` to `date` in local time: 0 today, -1 yesterday, 1 tomorrow. */
function calendarDaysBetween(now, date) {
  const midnight = (value) => new Date(value.getFullYear(), value.getMonth(), value.getDate()).getTime();
  return Math.round((midnight(date) - midnight(now)) / DAY_MS);
}

/** "today at 21:14", "yesterday at 21:14", "Mon 3 Oct at 21:14", in the user's locale. */
export function formatDateTime(value, now = new Date()) {
  const date = new Date(value);
  const time = TIME_FORMAT.format(date);

  switch (calendarDaysBetween(now, date)) {
    case 0:
      return `today at ${time}`;
    case -1:
      return `yesterday at ${time}`;
    case 1:
      return `tomorrow at ${time}`;
    default:
      return `${DATE_FORMAT.format(date)} at ${time}`;
  }
}

/** "in 3 hours", "5 minutes ago", "now" — how far `value` is from `now`, to the nearest unit. */
export function formatRelative(value, now = new Date()) {
  const minutes = Math.round((new Date(value).getTime() - now.getTime()) / 60_000);
  if (minutes === 0) return RELATIVE_FORMAT.format(0, 'second');
  if (Math.abs(minutes) < 60) return RELATIVE_FORMAT.format(minutes, 'minute');

  // Hours up to two days: a single day would read "tomorrow", which says less than "in 24 hours"
  // and repeats what formatDateTime already said beside it.
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 48) return RELATIVE_FORMAT.format(hours, 'hour');

  return RELATIVE_FORMAT.format(Math.round(hours / 24), 'day');
}

/**
 * The six things that can happen to a book in a sort, in the order they are shown and read out.
 * Every book lands in exactly one, so they add up to the books processed.
 */
export const RUN_COUNTS = [
  { key: 'new', label: 'New' },
  { key: 'updated', label: 'Updated' },
  { key: 'moved', label: 'Moved' },
  { key: 'upToDate', label: 'Up to date' },
  { key: 'notFound', label: 'Not found' },
  { key: 'failed', label: 'Failed' },
];

/** "1 book", "1,204 books". */
export function pluralBooks(count) {
  return `${count.toLocaleString()} book${count === 1 ? '' : 's'}`;
}

function countOf(counts, key) {
  return counts?.[key] || 0;
}

/** "3 new, 120 up to date" — only the outcomes that happened. */
function describeCounts(counts) {
  return RUN_COUNTS.filter(({ key }) => countOf(counts, key) > 0)
    .map(({ key, label }) => `${countOf(counts, key).toLocaleString()} ${label.toLowerCase()}`)
    .join(', ');
}

function booksProcessed(counts) {
  return RUN_COUNTS.reduce((sum, { key }) => sum + countOf(counts, key), 0);
}

/**
 * Not one book of the export was in the source folder. Undownloaded books do not explain that; a
 * source folder that is the wrong one (or, in a container, not mapped) does.
 */
function noneFound(counts) {
  const processed = booksProcessed(counts);
  return processed > 0 && countOf(counts, 'notFound') === processed;
}

/** Where to look when no book was found, in terms of where the source folder is set. */
function sourceFolderAdvice() {
  return isDesktop()
    ? 'Check that the source folder is the one OpenAudible downloads your books into.'
    : 'Check that the source folder is the one OpenAudible downloads your books into, and that SOURCE_PATH is mapped to it.';
}

/** What the user may want to act on or be told about, whichever way the run ended. */
function outcomeNotes(counts) {
  const moved = countOf(counts, 'moved');
  const notFound = countOf(counts, 'notFound');
  const failed = countOf(counts, 'failed');

  return [
    moved > 0 &&
      `${pluralBooks(moved)} that an older version left in the destination folder ${moved === 1 ? 'was' : 'were'} moved into ${moved === 1 ? 'its own folder' : 'their own folders'} there.`,
    notFound > 0 &&
      (noneFound(counts)
        ? `None of the books in the export were found in the source folder. ${sourceFolderAdvice()}`
        : `${pluralBooks(notFound)} in the export ${notFound === 1 ? 'has' : 'have'} no file in the source folder. These are usually books that have not been downloaded yet.`),
    failed > 0 && `${pluralBooks(failed)} could not be copied. The problems list says why.`,
  ].filter(Boolean);
}

/**
 * How a finished run went, in words: `{ headline, details, tone }`. The one description of a run,
 * used by the Progress card, the screen-reader announcement and the schedule's "Last sort" line,
 * so a run reads the same wherever it is mentioned. Takes a run status or a schedule's last-run
 * record; both carry the same counts, problemCount, isCanceled and error.
 *
 * `tone` is one of the banner tones: positive, caution or critical.
 */
export function summariseRun(run) {
  const counts = run?.counts;
  const processed = booksProcessed(counts);
  const notes = outcomeNotes(counts);
  const soFar = processed > 0 ? [`Before it stopped: ${describeCounts(counts)}.`] : [];

  if (run?.isCanceled) {
    return {
      // The backend gives a reason only when the user did not cancel ("Canceled because the app closed.").
      headline: run.error || `Sort canceled after ${pluralBooks(processed)}.`,
      details: [...soFar, ...notes, 'Books already copied are complete. The next sort picks up where this one stopped.'],
      tone: 'caution',
    };
  }

  if (run?.error) {
    return { headline: `Sort failed: ${run.error}`, details: [...soFar, ...notes], tone: 'critical' };
  }

  const tone =
    countOf(counts, 'failed') > 0 || noneFound(counts) ? 'critical' : run?.problemCount > 0 ? 'caution' : 'positive';
  const headline = noneFound(counts)
    ? `Sort complete, but no book was found in the source folder (${pluralBooks(processed)} in the export).`
    : processed > 0
      ? `Sort complete: ${describeCounts(counts)}.`
      : 'Sort complete: the export lists no books.';

  return { headline, details: notes, tone };
}
