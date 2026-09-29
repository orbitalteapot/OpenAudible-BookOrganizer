import { useId } from 'react';

/**
 * An on/off setting that takes effect at once. A real `role="switch"` button, so it is one tab
 * stop, toggles with Space or Enter, and is announced as "on" or "off" rather than "checked". The
 * thumb's position says the state as well as its colour.
 */
export default function Switch({ label, description, checked, onChange, disabled = false }) {
  const id = useId();
  const labelId = `${id}-label`;
  const descriptionId = description ? `${id}-description` : undefined;

  return (
    <div className="flex items-start justify-between gap-4">
      <div className="min-w-0">
        <span id={labelId} className="block text-sm text-fg">
          {label}
        </span>
        {description && (
          <span id={descriptionId} className="mt-0.5 block text-2xs text-fg-subtle">
            {description}
          </span>
        )}
      </div>

      <button
        type="button"
        role="switch"
        aria-checked={checked}
        aria-labelledby={labelId}
        aria-describedby={descriptionId}
        disabled={disabled}
        onClick={() => onChange(!checked)}
        className={[
          'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border transition-colors duration-150',
          'disabled:cursor-not-allowed disabled:opacity-45',
          checked ? 'border-accent bg-accent' : 'border-line-strong bg-raised',
        ].join(' ')}
      >
        <span
          aria-hidden="true"
          className={[
            'inline-block h-3.5 w-3.5 rounded-full transition-transform duration-150',
            checked ? 'translate-x-[18px] bg-accent-fg' : 'translate-x-0.5 bg-fg-muted',
          ].join(' ')}
        />
      </button>
    </div>
  );
}
