import { Library, ArrowUpDown, Headphones } from 'lucide-react';

const NAV_ITEMS = [
  { id: 'library', label: 'Library', icon: Library },
  { id: 'sort', label: 'Sort', icon: ArrowUpDown },
];

/**
 * Primary navigation. Collapses to an icon rail on a narrow window rather than holding a fixed
 * 240px, which on a small laptop was taking a fifth of the width to show two words.
 */
export default function Sidebar({ currentPage, onPageChange, bookCount }) {
  return (
    <nav
      aria-label="Main"
      className="flex w-14 shrink-0 flex-col border-r border-line bg-canvas lg:w-52"
    >
      <div className="flex h-14 items-center gap-2.5 px-4">
        <Headphones size={18} className="shrink-0 text-fg-muted" aria-hidden="true" />
        <span className="hidden truncate text-sm font-semibold text-fg lg:block">Organizer</span>
      </div>

      <ul className="flex-1 space-y-0.5 px-2">
        {NAV_ITEMS.map(({ id, label, icon: Icon }) => {
          const isActive = currentPage === id;

          return (
            <li key={id}>
              <button
                type="button"
                onClick={() => onPageChange(id)}
                aria-current={isActive ? 'page' : undefined}
                title={label}
                className={[
                  'flex h-9 w-full items-center gap-2.5 rounded px-3 text-sm transition-colors',
                  isActive
                    ? 'bg-raised font-medium text-fg'
                    : 'text-fg-muted hover:bg-raised/60 hover:text-fg',
                ].join(' ')}
              >
                <Icon size={16} className="shrink-0" aria-hidden="true" />
                <span className="hidden lg:block">{label}</span>
              </button>
            </li>
          );
        })}
      </ul>

      <div className="hidden border-t border-line px-5 py-3 lg:block">
        <p className="text-2xs text-fg-subtle">Books loaded</p>
        <p className="tabular text-sm font-medium text-fg">
          {bookCount > 0 ? bookCount.toLocaleString() : '—'}
        </p>
      </div>
    </nav>
  );
}
