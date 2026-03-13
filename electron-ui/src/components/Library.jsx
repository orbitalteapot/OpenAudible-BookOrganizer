import { useState, useMemo } from 'react';
import {
  Search,
  FileUp,
  ChevronUp,
  ChevronDown,
  BookOpen,
  Star,
  Clock,
  User,
} from 'lucide-react';
import { parseBooks } from '../api';

export default function LibraryView({ books, setBooks, sortState }) {
  const [search, setSearch] = useState('');
  const [sortField, setSortField] = useState('title');
  const [sortDir, setSortDir] = useState('asc');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState(null);
  const isElectron = typeof window !== 'undefined' && !!window.electronAPI;

  const handleLoadCsv = async () => {
    let filePath = sortState?.csvPath;

    if (isElectron) {
      filePath = await window.electronAPI?.openFile([
        { name: 'CSV Files', extensions: ['csv'] },
      ]);
      if (!filePath) return;
    } else if (!filePath) {
      setError('No CSV path is configured for web mode');
      return;
    }

    setLoading(true);
    setError(null);
    try {
      const data = await parseBooks(filePath);
      setBooks(data);
    } catch (err) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  const handleSort = (field) => {
    if (sortField === field) {
      setSortDir((d) => (d === 'asc' ? 'desc' : 'asc'));
    } else {
      setSortField(field);
      setSortDir('asc');
    }
  };

  const SortIcon = ({ field }) => {
    if (sortField !== field) return <ChevronUp size={12} className="opacity-0 group-hover:opacity-30" />;
    return sortDir === 'asc' ? (
      <ChevronUp size={12} className="text-brand-400" />
    ) : (
      <ChevronDown size={12} className="text-brand-400" />
    );
  };

  const filtered = useMemo(() => {
    let result = [...books];

    if (search.trim()) {
      const q = search.toLowerCase();
      result = result.filter(
        (b) =>
          b.title?.toLowerCase().includes(q) ||
          b.author?.toLowerCase().includes(q) ||
          b.seriesName?.toLowerCase().includes(q) ||
          b.narratedBy?.toLowerCase().includes(q)
      );
    }

    result.sort((a, b) => {
      const aVal = (a[sortField] || '').toString().toLowerCase();
      const bVal = (b[sortField] || '').toString().toLowerCase();
      const cmp = aVal.localeCompare(bVal);
      return sortDir === 'asc' ? cmp : -cmp;
    });

    return result;
  }, [books, search, sortField, sortDir]);

  // Empty state
  if (books.length === 0) {
    return (
      <div className="flex-1 flex items-center justify-center">
        <div className="text-center max-w-md">
          <div className="mx-auto w-20 h-20 rounded-2xl bg-gradient-to-br from-brand-500/20 to-violet-500/20 border border-brand-500/20 flex items-center justify-center mb-6">
            <BookOpen size={36} className="text-brand-400" />
          </div>
          <h2 className="text-xl font-bold text-white mb-2">No audiobooks loaded</h2>
          <p className="text-sm text-slate-400 mb-8 leading-relaxed">
            {isElectron
              ? 'Import your OpenAudible CSV export to see your library here. You can then browse, search, and sort your audiobook collection.'
              : 'Load the configured OpenAudible CSV export to browse your library and trigger sorting from the web interface.'}
          </p>
          {!isElectron && sortState?.csvPath && (
            <p className="mb-4 text-xs text-slate-500 bg-slate-800/50 border border-slate-700/30 rounded-lg px-4 py-2 break-all">
              CSV: {sortState.csvPath}
            </p>
          )}
          <button onClick={handleLoadCsv} disabled={loading} className="btn-primary inline-flex items-center gap-2">
            <FileUp size={16} />
            {loading ? 'Loading...' : isElectron ? 'Load CSV Export' : 'Load Library'}
          </button>
          {error && (
            <p className="mt-4 text-sm text-red-400 bg-red-500/10 border border-red-500/20 rounded-lg px-4 py-2">
              {error}
            </p>
          )}
        </div>
      </div>
    );
  }

  const columns = [
    { key: 'title', label: 'Title', width: 'flex-[2.5]' },
    { key: 'author', label: 'Author', width: 'flex-[1.5]' },
    { key: 'narratedBy', label: 'Narrator', width: 'flex-[1.5]' },
    { key: 'seriesName', label: 'Series', width: 'flex-1' },
    { key: 'duration', label: 'Duration', width: 'w-28' },
    { key: 'aveRating', label: 'Rating', width: 'w-24' },
  ];

  return (
    <div className="flex-1 flex flex-col min-h-0">
      {/* Header */}
      <div className="flex items-center justify-between mb-5">
        <div>
          <h2 className="text-lg font-bold text-white">Library</h2>
          <p className="text-xs text-slate-400 mt-0.5">
            {filtered.length} of {books.length} audiobook{books.length !== 1 ? 's' : ''}
          </p>
        </div>
        <div className="flex items-center gap-3">
          <div className="relative">
            <Search size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
            <input
              type="text"
              placeholder="Search books..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="input-field pl-9 w-64 !py-2 text-sm"
            />
          </div>
          <button onClick={handleLoadCsv} disabled={loading} className="btn-secondary inline-flex items-center gap-2 text-sm !py-2">
            <FileUp size={14} />
            {loading ? 'Loading...' : isElectron ? 'Reload' : 'Reload Library'}
          </button>
        </div>
      </div>

      {error && (
        <div className="mb-4 text-sm text-red-400 bg-red-500/10 border border-red-500/20 rounded-lg px-4 py-2">
          {error}
        </div>
      )}

      {/* Table */}
      <div className="glass-card flex-1 flex flex-col min-h-0 overflow-hidden">
        {/* Table Header */}
        <div className="flex items-center px-5 py-3 border-b border-slate-700/50 bg-slate-800/40 text-xs font-semibold text-slate-400 uppercase tracking-wider">
          {columns.map((col) => (
            <button
              key={col.key}
              onClick={() => handleSort(col.key)}
              className={`group flex items-center gap-1.5 ${col.width} hover:text-slate-200 transition-colors`}
            >
              {col.label}
              <SortIcon field={col.key} />
            </button>
          ))}
        </div>

        {/* Table Body */}
        <div className="flex-1 overflow-y-auto">
          {filtered.map((book, i) => (
            <div
              key={book.key || book.asin || i}
              className="flex items-center px-5 py-3 border-b border-slate-700/20 hover:bg-slate-700/20 transition-colors group"
            >
              <div className="flex-[2.5] pr-3">
                <p className="text-sm font-medium text-slate-100 truncate group-hover:text-white transition-colors">
                  {book.title || 'Untitled'}
                </p>
                {book.shortTitle && book.shortTitle !== book.title && (
                  <p className="text-[11px] text-slate-500 truncate">{book.shortTitle}</p>
                )}
              </div>
              <div className="flex-[1.5] pr-3">
                <div className="flex items-center gap-1.5">
                  <User size={12} className="text-slate-500 shrink-0" />
                  <span className="text-sm text-slate-300 truncate">{book.author || '—'}</span>
                </div>
              </div>
              <div className="flex-[1.5] pr-3">
                <span className="text-sm text-slate-400 truncate block">{book.narratedBy || '—'}</span>
              </div>
              <div className="flex-1 pr-3">
                <span className="text-sm text-slate-400 truncate block">
                  {book.seriesName
                    ? `${book.seriesName}${book.seriesSequence ? ` #${book.seriesSequence}` : ''}`
                    : '—'}
                </span>
              </div>
              <div className="w-28">
                <div className="flex items-center gap-1.5 text-sm text-slate-400">
                  <Clock size={12} className="text-slate-500 shrink-0" />
                  <span className="truncate">{book.duration || '—'}</span>
                </div>
              </div>
              <div className="w-24">
                {book.aveRating > 0 ? (
                  <div className="flex items-center gap-1.5 text-sm">
                    <Star size={12} className="text-amber-400 fill-amber-400 shrink-0" />
                    <span className="text-slate-300">{book.aveRating.toFixed(1)}</span>
                  </div>
                ) : (
                  <span className="text-sm text-slate-500">—</span>
                )}
              </div>
            </div>
          ))}

          {filtered.length === 0 && books.length > 0 && (
            <div className="text-center py-16 text-slate-500">
              <Search size={32} className="mx-auto mb-3 opacity-40" />
              <p className="text-sm">No books match your search</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
