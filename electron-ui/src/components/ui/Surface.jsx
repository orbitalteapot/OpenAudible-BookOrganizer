import { AlertCircle, CheckCircle2, Info } from 'lucide-react';

/** A plain panel. Flat, hairline border, no blur or glow. */
export function Card({ title, description, actions, children, className = '', bodyClassName = '' }) {
  return (
    <section className={`flex min-h-0 flex-col rounded-lg border border-line bg-surface ${className}`}>
      {(title || actions) && (
        <header className="flex items-start justify-between gap-4 border-b border-line px-5 py-3.5">
          <div className="min-w-0">
            {title && <h2 className="text-base font-semibold text-fg">{title}</h2>}
            {description && <p className="mt-0.5 text-xs text-fg-muted">{description}</p>}
          </div>
          {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
        </header>
      )}
      <div className={`min-h-0 flex-1 p-5 ${bodyClassName}`}>{children}</div>
    </section>
  );
}

const BANNER_TONES = {
  critical: { icon: AlertCircle, cls: 'border-critical/30 bg-critical/10 text-critical' },
  caution: { icon: Info, cls: 'border-caution/30 bg-caution/10 text-caution' },
  positive: { icon: CheckCircle2, cls: 'border-positive/30 bg-positive/10 text-positive' },
};

/**
 * An inline message. Every tone carries an icon as well as a colour, so the meaning survives for
 * anyone who cannot distinguish the hues.
 */
export function Banner({ tone = 'critical', children, className = '' }) {
  const { icon: Icon, cls } = BANNER_TONES[tone] ?? BANNER_TONES.critical;

  return (
    <div
      role={tone === 'critical' ? 'alert' : 'status'}
      className={`flex items-start gap-2.5 rounded border px-3.5 py-2.5 text-sm ${cls} ${className}`}
    >
      <Icon size={15} className="mt-0.5 shrink-0" aria-hidden="true" />
      <span className="min-w-0">{children}</span>
    </div>
  );
}

/** Centred placeholder for a view with nothing in it yet. */
export function EmptyState({ icon: Icon, title, description, children }) {
  return (
    <div className="flex flex-1 items-center justify-center p-8">
      <div className="max-w-sm text-center">
        {Icon && (
          <div className="mx-auto mb-5 flex h-12 w-12 items-center justify-center rounded-lg border border-line bg-raised">
            <Icon size={22} className="text-fg-muted" aria-hidden="true" />
          </div>
        )}
        <h2 className="text-base font-semibold text-fg">{title}</h2>
        {description && <p className="mx-auto mt-2 text-sm leading-relaxed text-fg-muted">{description}</p>}
        {children && <div className="mt-6 flex flex-col items-center gap-3">{children}</div>}
      </div>
    </div>
  );
}

const STAT_TONES = {
  default: 'text-fg',
  accent: 'text-accent',
  positive: 'text-positive',
  caution: 'text-caution',
  critical: 'text-critical',
};

/** One number with its label. Muted when zero so the eye lands on what actually happened. */
export function Stat({ label, value, tone = 'default' }) {
  const isZero = value === 0;

  return (
    <div className="rounded border border-line bg-raised px-3 py-2.5">
      <p className="text-2xs text-fg-subtle">{label}</p>
      <p className={`tabular mt-0.5 text-lg font-semibold ${isZero ? 'text-fg-subtle' : STAT_TONES[tone]}`}>
        {typeof value === 'number' ? value.toLocaleString() : value}
      </p>
    </div>
  );
}

export function ProgressBar({ value, tone = 'accent', label }) {
  const clamped = Math.max(0, Math.min(100, value || 0));
  const fill = {
    accent: 'bg-accent',
    positive: 'bg-positive',
    caution: 'bg-caution',
    critical: 'bg-critical',
  }[tone];

  return (
    <div
      role="progressbar"
      aria-valuenow={Math.round(clamped)}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-label={label}
      className="h-1.5 w-full overflow-hidden rounded-full bg-line"
    >
      <div
        className={`h-full rounded-full transition-[width] duration-300 ease-out ${fill}`}
        style={{ width: `${clamped}%` }}
      />
    </div>
  );
}
