import { Library, ArrowUpDown, Headphones } from 'lucide-react';

const navItems = [
  { id: 'library', label: 'Library', icon: Library },
  { id: 'sort', label: 'Sort Files', icon: ArrowUpDown },
];

export default function Sidebar({ currentPage, onPageChange, bookCount }) {
  return (
    <aside className="w-60 bg-slate-800/40 border-r border-slate-700/40 flex flex-col shrink-0">
      {/* Branding */}
      <div className="p-5 pb-6">
        <div className="flex items-center gap-3">
          <div className="w-10 h-10 rounded-xl bg-gradient-to-br from-brand-500 via-brand-600 to-violet-600 flex items-center justify-center shadow-lg shadow-brand-500/25">
            <Headphones size={20} className="text-white" />
          </div>
          <div>
            <h1 className="text-sm font-bold text-white leading-tight">OpenAudible</h1>
            <p className="text-[11px] text-slate-400 leading-tight">Book Organizer</p>
          </div>
        </div>
      </div>

      {/* Navigation */}
      <nav className="flex-1 px-3 space-y-1">
        <p className="px-3 mb-2 text-[10px] font-semibold uppercase tracking-wider text-slate-500">
          Menu
        </p>
        {navItems.map((item) => {
          const Icon = item.icon;
          const isActive = currentPage === item.id;
          return (
            <button
              key={item.id}
              onClick={() => onPageChange(item.id)}
              className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm font-medium transition-all duration-200 ${
                isActive
                  ? 'bg-brand-500/15 text-brand-400 shadow-sm'
                  : 'text-slate-400 hover:bg-slate-700/40 hover:text-slate-200'
              }`}
            >
              <Icon size={18} strokeWidth={isActive ? 2.2 : 1.8} />
              <span>{item.label}</span>
            </button>
          );
        })}
      </nav>

      {/* Footer stats */}
      <div className="p-4 mx-3 mb-3 rounded-xl bg-slate-800/80 border border-slate-700/40">
        <div className="flex items-center justify-between">
          <span className="text-xs text-slate-500">Books loaded</span>
          <span className="text-xs font-bold text-brand-400">
            {bookCount > 0 ? bookCount.toLocaleString() : '—'}
          </span>
        </div>
      </div>
    </aside>
  );
}
