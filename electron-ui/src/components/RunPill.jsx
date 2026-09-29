import { Loader2 } from 'lucide-react';
import { isRunning } from '../hooks';

/**
 * A small reminder, on every page, that a sort is running, which takes you to it. Shows only the
 * percentage on the narrow icon rail.
 */
export default function RunPill({ status, onOpen }) {
  if (!isRunning(status)) return null;

  const percent = `${Math.round(status.percentage || 0)}%`;
  const text = status.trigger === 'scheduled' ? `Automatic sort running · ${percent}` : `Sorting… ${percent}`;

  return (
    <button
      type="button"
      onClick={onOpen}
      aria-label={`${text}. Show progress`}
      title={text}
      // Body-coloured text on the tint: accent text on its own tint falls under 4.5:1 in light mode.
      // Stacked with no side padding on the icon rail: its 40px leaves no room for the spinner and
      // the percentage side by side, which spilled both past the border.
      className="flex min-h-8 w-full flex-col items-center justify-center gap-0.5 rounded border border-accent/40 bg-accent/10 py-1 text-2xs font-medium text-fg hover:bg-accent/15 xl:flex-row xl:justify-start xl:gap-2 xl:px-2"
    >
      <Loader2 size={14} className="shrink-0 animate-spin text-accent" aria-hidden="true" />
      <span className="tabular hidden truncate xl:inline">{text}</span>
      <span className="tabular xl:hidden">{percent}</span>
    </button>
  );
}
