import { ArrowUpDown, Headphones, Library, Monitor, Moon, Sun } from 'lucide-react';
import RunPill from './RunPill';
import SegmentedControl from './ui/SegmentedControl';

const NAV_ITEMS = [
  { id: 'library', label: 'Library', icon: Library },
  { id: 'sort', label: 'Sort', icon: ArrowUpDown },
];

const THEME_OPTIONS = [
  { value: 'system', label: 'System theme', icon: Monitor },
  { value: 'light', label: 'Light theme', icon: Sun },
  { value: 'dark', label: 'Dark theme', icon: Moon },
];

/**
 * Primary navigation, with what is true on every page: a sort in progress, the books loaded, and
 * the theme. Collapses to an icon rail on a narrow window rather than holding a fixed 240px, which
 * on a small laptop was taking a fifth of the width to show two words.
 */
export default function Sidebar({ currentPage, onPageChange, bookCount, runStatus, theme, onThemeChange }) {
  return (
    <nav aria-label="Main" className="flex w-14 shrink-0 flex-col border-r border-line bg-canvas xl:w-52">
      <div className="flex h-14 items-center gap-3 px-4">
        <Headphones size={20} className="shrink-0 text-accent" aria-hidden="true" />
        <span className="hidden truncate text-sm font-semibold text-fg xl:block">Organizer</span>
      </div>

      <ul className="flex-1 space-y-1 px-2">
        {NAV_ITEMS.map(({ id, label, icon: Icon }) => {
          const isActive = currentPage === id;

          return (
            <li key={id}>
              <button
                type="button"
                onClick={() => onPageChange(id)}
                aria-current={isActive ? 'page' : undefined}
                // The label is display:none on the icon rail, which takes it out of the
                // accessibility tree as well, leaving the button named only by its tooltip.
                aria-label={label}
                title={label}
                className={[
                  'flex h-9 w-full items-center gap-3 rounded px-3 text-sm transition-colors',
                  isActive ? 'bg-raised font-medium text-fg' : 'text-fg-muted hover:bg-raised/60 hover:text-fg',
                ].join(' ')}
              >
                <Icon size={16} className={`shrink-0 ${isActive ? 'text-accent' : ''}`} aria-hidden="true" />
                <span className="hidden xl:block">{label}</span>
              </button>
            </li>
          );
        })}
      </ul>

      <div className="space-y-3 border-t border-line px-2 py-3 xl:px-3">
        <RunPill status={runStatus} onOpen={() => onPageChange('sort')} />

        <div className="hidden px-2 xl:block">
          <p className="text-2xs text-fg-subtle">Books loaded</p>
          <p className="tabular text-sm font-medium text-fg">{bookCount > 0 ? bookCount.toLocaleString() : '—'}</p>
        </div>

        <SegmentedControl
          label="Theme"
          value={theme}
          options={THEME_OPTIONS}
          onChange={onThemeChange}
          iconOnly
          className="flex flex-col xl:flex-row"
        />
      </div>
    </nav>
  );
}
