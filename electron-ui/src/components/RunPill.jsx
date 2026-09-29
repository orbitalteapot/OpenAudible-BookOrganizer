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
      className="flex min-h-8 w-full items-center justify-center gap-2 rounded border border-accent/40 bg-accent/10 px-2 text-2xs font-medium text-fg hover:bg-accent/15 xl:justify-start"
    >
      <Loader2 size={14} className="shrink-0 animate-spin text-accent" aria-hidden="true" />
      <span className="tabular hidden truncate xl:inline">{text}</span>
      <span className="tabular xl:hidden">{percent}</span>
    </button>
  );
}
