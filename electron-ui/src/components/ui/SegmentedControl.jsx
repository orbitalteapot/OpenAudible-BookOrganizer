/**
 * A two-or-more way choice, rendered as a real radiogroup so arrow keys move between options and
 * screen readers announce the selection. Roving tabindex keeps the group a single tab stop.
 */
export default function SegmentedControl({ label, value, options, onChange, disabled = false, name }) {
  const currentIndex = Math.max(0, options.findIndex((option) => option.value === value));

  const move = (delta) => {
    const next = options[(currentIndex + delta + options.length) % options.length];
    if (next) onChange(next.value);
  };

  const handleKeyDown = (event) => {
    if (event.key === 'ArrowRight' || event.key === 'ArrowDown') {
      event.preventDefault();
      move(1);
    } else if (event.key === 'ArrowLeft' || event.key === 'ArrowUp') {
      event.preventDefault();
      move(-1);
    }
  };

  return (
    <div
      role="radiogroup"
      aria-label={label}
      onKeyDown={handleKeyDown}
      className="flex gap-1 rounded border border-line bg-surface p-1"
    >
      {options.map((option) => {
        const selected = option.value === value;
        const Icon = option.icon;

        return (
          <button
            key={option.value}
            type="button"
            role="radio"
            name={name}
            aria-checked={selected}
            tabIndex={selected ? 0 : -1}
            disabled={disabled}
            onClick={() => onChange(option.value)}
            className={[
              'inline-flex flex-1 items-center justify-center gap-1.5 rounded-sm px-3 py-1.5',
              'text-sm transition-colors duration-150',
              'disabled:cursor-not-allowed disabled:opacity-45',
              selected ? 'bg-accent text-accent-fg font-medium' : 'text-fg-muted hover:bg-raised hover:text-fg',
            ].join(' ')}
          >
            {Icon && <Icon size={14} aria-hidden="true" />}
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
