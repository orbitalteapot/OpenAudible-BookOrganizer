import { useEffect, useRef, useCallback } from 'react';
import {
  FolderOpen,
  FileSpreadsheet,
  FolderOutput,
  Play,
  Square,
  CheckCircle2,
  AlertCircle,
  Loader2,
  ArrowUpDown,
} from 'lucide-react';
import { startSort, getSortProgress, cancelSort } from '../api';

export default function SortPanel({ books, sortState, setSortState }) {
  const { csvPath, sourcePath, destPath, sorting, progress, error } = sortState;
  const pollRef = useRef(null);

  const update = useCallback((patch) => {
    setSortState((prev) => ({ ...prev, ...patch }));
  }, [setSortState]);

  const handleBrowseCsv = async () => {
    const path = await window.electronAPI?.openFile([
      { name: 'CSV Files', extensions: ['csv'] },
    ]);
    if (path) update({ csvPath: path });
  };

  const handleBrowseSource = async () => {
    const path = await window.electronAPI?.openFolder();
    if (path) update({ sourcePath: path });
  };

  const handleBrowseDest = async () => {
    const path = await window.electronAPI?.openFolder();
    if (path) update({ destPath: path });
  };

  const handleStartSort = async () => {
    if (!csvPath || !sourcePath || !destPath) {
      update({ error: 'All three paths are required' });
      return;
    }

    update({ error: null, sorting: true, progress: null });

    try {
      await startSort(csvPath, sourcePath, destPath);
      startPolling();
    } catch (err) {
      update({ error: err.message, sorting: false });
    }
  };

  const handleCancelSort = async () => {
    try {
      await cancelSort();
      update({ error: null });
    } catch (err) {
      update({ error: err.message });
    }
  };

  const startPolling = useCallback(() => {
    if (pollRef.current) clearInterval(pollRef.current);
    pollRef.current = setInterval(async () => {
      try {
        const p = await getSortProgress();
        const patches = { progress: p };
        if (p.isComplete) {
          clearInterval(pollRef.current);
          pollRef.current = null;
          patches.sorting = false;
          if (p.error) patches.error = p.error;
        }
        setSortState((prev) => ({ ...prev, ...patches }));
      } catch {
        // Ignore polling errors
      }
    }, 400);
  }, [setSortState]);

  // Resume polling when component remounts while a sort is still active
  useEffect(() => {
    if (sorting && !pollRef.current) {
      startPolling();
    }
    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
  }, [sorting, startPolling]);

  const isCanceled = progress?.isCanceled;
  const isComplete = progress?.isComplete && !progress?.error && !isCanceled;
  const hasError = progress?.error;

  return (
    <div className="flex-1 flex flex-col min-h-0 overflow-y-auto">
      <div className="mb-6">
        <h2 className="text-lg font-bold text-white">Sort Audiobooks</h2>
        <p className="text-xs text-slate-400 mt-0.5">
          Organize your audiobook files into Author / Series / Book folder structure
        </p>
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-6">
        {/* Configuration Card */}
        <div className="glass-card p-6">
          <h3 className="text-sm font-semibold text-slate-200 mb-5">Configuration</h3>

          <div className="space-y-4">
            {/* CSV File */}
            <div>
              <label className="block text-xs font-medium text-slate-400 mb-1.5">
                OpenAudible CSV Export
              </label>
              <div className="flex gap-2">
                <div className="relative flex-1">
                  <FileSpreadsheet size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
                  <input
                    type="text"
                    value={csvPath}
                    placeholder="Select CSV export file..."
                    className="input-field pl-9 text-sm"
                    readOnly
                  />
                </div>
                <button onClick={handleBrowseCsv} className="btn-secondary text-sm whitespace-nowrap">
                  Browse
                </button>
              </div>
            </div>

            {/* Source Folder */}
            <div>
              <label className="block text-xs font-medium text-slate-400 mb-1.5">
                Source Audio Folder
              </label>
              <div className="flex gap-2">
                <div className="relative flex-1">
                  <FolderOpen size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
                  <input
                    type="text"
                    value={sourcePath}
                    placeholder="Select source audio folder..."
                    className="input-field pl-9 text-sm"
                    readOnly
                  />
                </div>
                <button onClick={handleBrowseSource} className="btn-secondary text-sm whitespace-nowrap">
                  Browse
                </button>
              </div>
            </div>

            {/* Destination Folder */}
            <div>
              <label className="block text-xs font-medium text-slate-400 mb-1.5">
                Destination Folder
              </label>
              <div className="flex gap-2">
                <div className="relative flex-1">
                  <FolderOutput size={15} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-500" />
                  <input
                    type="text"
                    value={destPath}
                    placeholder="Select destination folder..."
                    className="input-field pl-9 text-sm"
                    readOnly
                  />
                </div>
                <button onClick={handleBrowseDest} className="btn-secondary text-sm whitespace-nowrap">
                  Browse
                </button>
              </div>
            </div>
          </div>

          {error && (
            <div className="mt-4 flex items-start gap-2 text-sm text-red-400 bg-red-500/10 border border-red-500/20 rounded-lg px-4 py-3">
              <AlertCircle size={16} className="shrink-0 mt-0.5" />
              <span>{error}</span>
            </div>
          )}

          <div className="mt-6 flex gap-3">
            <button
              onClick={handleStartSort}
              disabled={sorting || !csvPath || !sourcePath || !destPath}
              className="btn-primary flex-1 inline-flex items-center justify-center gap-2"
            >
              {sorting ? (
                <>
                  <Loader2 size={16} className="animate-spin" />
                  Sorting...
                </>
              ) : (
                <>
                  <Play size={16} />
                  Start Sorting
                </>
              )}
            </button>

            {sorting && (
              <button
                onClick={handleCancelSort}
                className="btn-secondary inline-flex items-center justify-center gap-2 px-4"
              >
                <Square size={16} />
                Cancel
              </button>
            )}
          </div>
        </div>

        {/* Progress Card */}
        <div className="glass-card p-6">
          <h3 className="text-sm font-semibold text-slate-200 mb-5">Progress</h3>

          {!progress && !sorting && (
            <div className="flex flex-col items-center justify-center py-12 text-center">
              <div className="w-14 h-14 rounded-xl bg-slate-700/50 border border-slate-600/30 flex items-center justify-center mb-4">
                <ArrowUpDown size={24} className="text-slate-500" />
              </div>
              <p className="text-sm text-slate-500 max-w-xs">
                Configure your paths and click Start to begin organizing your audiobooks
              </p>
            </div>
          )}

          {(sorting || progress) && (
            <div className="space-y-5">
              {/* Progress Bar */}
              <div>
                <div className="flex items-center justify-between mb-2">
                  <span className="text-xs text-slate-400">
                    {isComplete ? 'Complete' : isCanceled ? 'Canceled' : hasError ? 'Error' : 'Sorting...'}
                  </span>
                  <span className="text-xs font-bold text-brand-400">
                    {(progress?.percentage || 0).toFixed(1)}%
                  </span>
                </div>
                <div className="h-2.5 bg-slate-700/60 rounded-full overflow-hidden">
                  <div
                    className={`h-full rounded-full transition-all duration-500 ease-out ${
                      hasError
                        ? 'bg-red-500'
                        : isCanceled
                        ? 'bg-amber-500'
                        : isComplete
                        ? 'bg-emerald-500'
                        : 'bg-gradient-to-r from-brand-600 to-brand-400'
                    }`}
                    style={{ width: `${progress?.percentage || 0}%` }}
                  />
                </div>
              </div>

              {/* Stats */}
              <div className="grid grid-cols-3 gap-3">
                <StatCard label="Total" value={progress?.totalBooks || 0} />
                <StatCard
                  label="Copied"
                  value={progress?.copiedBooks || 0}
                  color="text-emerald-400"
                />
                <StatCard
                  label="Skipped"
                  value={
                    (progress?.currentBook || 0) - (progress?.copiedBooks || 0)
                  }
                  color="text-amber-400"
                />
              </div>

              {/* Current File */}
              {progress?.currentTitle && !isComplete && (
                <div className="bg-slate-800/50 rounded-lg px-4 py-3 border border-slate-700/30">
                  <p className="text-[11px] text-slate-500 mb-0.5">Current file</p>
                  <p className="text-sm text-slate-300 truncate">{progress.currentTitle}</p>
                </div>
              )}

              {/* Complete State */}
              {isComplete && (
                <div className="bg-emerald-500/10 border border-emerald-500/20 rounded-lg px-4 py-4 flex items-center gap-3">
                  <CheckCircle2 size={20} className="text-emerald-400 shrink-0" />
                  <div>
                    <p className="text-sm font-medium text-emerald-300">Sorting complete!</p>
                    <p className="text-xs text-emerald-400/70 mt-0.5">
                      {progress.copiedBooks} file{progress.copiedBooks !== 1 ? 's' : ''} copied successfully
                    </p>
                  </div>
                </div>
              )}

              {isCanceled && (
                <div className="bg-amber-500/10 border border-amber-500/20 rounded-lg px-4 py-4 flex items-center gap-3">
                  <AlertCircle size={20} className="text-amber-400 shrink-0" />
                  <div>
                    <p className="text-sm font-medium text-amber-300">Sorting canceled</p>
                    <p className="text-xs text-amber-400/70 mt-0.5">
                      {progress?.copiedBooks || 0} file{(progress?.copiedBooks || 0) !== 1 ? 's' : ''} copied before cancellation
                    </p>
                  </div>
                </div>
              )}

              {/* Error State */}
              {hasError && (
                <div className="bg-red-500/10 border border-red-500/20 rounded-lg px-4 py-4 flex items-center gap-3">
                  <AlertCircle size={20} className="text-red-400 shrink-0" />
                  <div>
                    <p className="text-sm font-medium text-red-300">Sort failed</p>
                    <p className="text-xs text-red-400/70 mt-0.5">{progress.error}</p>
                  </div>
                </div>
              )}
            </div>
          )}
        </div>
      </div>
    </div>
  );
}

function StatCard({ label, value, color = 'text-white' }) {
  return (
    <div className="bg-slate-800/50 rounded-lg px-4 py-3 border border-slate-700/30">
      <p className="text-[11px] text-slate-500 mb-0.5">{label}</p>
      <p className={`text-lg font-bold ${color}`}>{value.toLocaleString()}</p>
    </div>
  );
}
