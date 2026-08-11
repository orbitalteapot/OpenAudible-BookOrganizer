// OpenAudible writes durations as prose: "12 hrs and 34 mins", "45 mins", "1 hr and 1 min".
// At column width that truncates to "12 hrs and…", which tells the reader nothing, so it is
// rewritten compactly and the raw value kept as the cell's tooltip.
const DURATION_PATTERN = /(?:(\d+)\s*h(?:ou)?rs?)?\D*(?:(\d+)\s*min(?:ute)?s?)?/i;

export function formatDuration(raw) {
  if (!raw) return '—';

  const text = String(raw).trim();
  const match = DURATION_PATTERN.exec(text);
  if (!match) return text;

  const hours = Number(match[1] || 0);
  const minutes = Number(match[2] || 0);

  if (!hours && !minutes) return text;
  if (!hours) return `${minutes}m`;
  if (!minutes) return `${hours}h`;

  return `${hours}h ${minutes}m`;
}
