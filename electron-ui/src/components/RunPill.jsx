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
      className="flex min-h-[32px] w-full items-center justify-center gap-2 rounded border border-accent/40 bg-accent/10 px-2 text-2xs font-medium text-accent hover:bg-accent/20 xl:justify-start"
    >
      <Loader2 size={13} className="shrink-0 animate-spin" aria-hidden="true" />
      <span className="tabular hidden truncate xl:inline">{text}</span>
      <span className="tabular xl:hidden">{percent}</span>
    </button>
  );
}
