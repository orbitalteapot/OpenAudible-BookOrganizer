import { useId, useState } from 'react';
import { ChevronRight } from 'lucide-react';

/**
 * A summary line that shows or hides more detail. A plain button with aria-expanded and
 * aria-controls rather than <details>, whose open state some screen readers do not announce.
 */
export default function Disclosure({ summary, children, defaultOpen = false, className = '' }) {
  const [open, setOpen] = useState(defaultOpen);
  const panelId = useId();

  return (
    <div className={className}>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((value) => !value)}
        className="inline-flex min-h-8 items-center gap-1 rounded text-sm font-medium text-fg-muted hover:text-fg"
      >
        <ChevronRight
          size={14}
          aria-hidden="true"
          className={`shrink-0 transition-transform duration-150 ${open ? 'rotate-90' : ''}`}
        />
        {summary}
      </button>
      <div id={panelId} hidden={!open} className="mt-2">
        {children}
      </div>
    </div>
  );
}
