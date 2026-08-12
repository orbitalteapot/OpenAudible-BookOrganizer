// OpenAudible writes durations as prose: "12 hrs and 34 mins", "45 mins", "1 hr and 1 min".
const DURATION_PATTERN = /(?:(\d+)\s*h(?:ou)?rs?)?\D*(?:(\d+)\s*min(?:ute)?s?)?/i;

/**
 * Total length in minutes, or null when the value is not a duration we recognise.
 *
 * Sorting needs this rather than the text: comparing "45 mins" against "9 hrs and 9 mins" as
 * strings — even with numeric collation — reads 45 against 9 and files the three quarters of an
 * hour after the nine hours.
 */
export function durationMinutes(raw) {
  if (!raw) return null;

  const match = DURATION_PATTERN.exec(String(raw).trim());
  if (!match) return null;

  const hours = Number(match[1] || 0);
  const minutes = Number(match[2] || 0);
  if (!hours && !minutes) return null;

  return hours * 60 + minutes;
}

/**
 * At column width the raw text truncates to "12 hrs and…", which tells the reader nothing, so it
 * is rewritten compactly. Anything unrecognised is passed through unchanged rather than dropped.
 */
export function formatDuration(raw) {
  if (!raw) return '—';

  const text = String(raw).trim();
  const total = durationMinutes(text);
  if (total === null) return text;

  const hours = Math.floor(total / 60);
  const minutes = total % 60;

  if (!hours) return `${minutes}m`;
  if (!minutes) return `${hours}h`;

  return `${hours}h ${minutes}m`;
}
