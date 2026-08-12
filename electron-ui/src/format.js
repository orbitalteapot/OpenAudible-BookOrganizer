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
