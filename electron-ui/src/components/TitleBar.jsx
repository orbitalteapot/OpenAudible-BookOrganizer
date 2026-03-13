import { useState } from 'react';
import { Minus, Square, X, Copy } from 'lucide-react';

export default function TitleBar() {
  const [isMaximized, setIsMaximized] = useState(false);
  const isElectron = typeof window !== 'undefined' && !!window.electronAPI;

  const handleMinimize = () => window.electronAPI?.minimize();
  const handleMaximize = async () => {
    await window.electronAPI?.maximize();
    setIsMaximized(await window.electronAPI?.isMaximized());
  };
  const handleClose = () => window.electronAPI?.close();

  if (!isElectron) {
    return (
      <header className="h-10 bg-slate-900/90 border-b border-slate-800 flex items-center px-4 select-none shrink-0">
        <div className="flex items-center gap-2.5">
          <div className="w-5 h-5 rounded-md bg-gradient-to-br from-brand-500 to-violet-500 flex items-center justify-center">
            <span className="text-[10px]">OA</span>
          </div>
          <span className="text-xs font-medium text-slate-400">OpenAudible Book Organizer</span>
        </div>
      </header>
    );
  }

  return (
    <header className="titlebar-drag h-10 bg-slate-900/90 border-b border-slate-800 flex items-center justify-between px-4 select-none shrink-0">
      <div className="flex items-center gap-2.5">
        <div className="w-5 h-5 rounded-md bg-gradient-to-br from-brand-500 to-violet-500 flex items-center justify-center">
          <span className="text-[10px]">🎧</span>
        </div>
        <span className="text-xs font-medium text-slate-400">OpenAudible Book Organizer</span>
      </div>

      <div className="titlebar-no-drag flex items-center">
        <button
          onClick={handleMinimize}
          className="w-11 h-10 flex items-center justify-center text-slate-400 hover:bg-slate-700/60 hover:text-slate-200 transition-colors"
        >
          <Minus size={14} />
        </button>
        <button
          onClick={handleMaximize}
          className="w-11 h-10 flex items-center justify-center text-slate-400 hover:bg-slate-700/60 hover:text-slate-200 transition-colors"
        >
          {isMaximized ? <Copy size={12} /> : <Square size={12} />}
        </button>
        <button
          onClick={handleClose}
          className="w-11 h-10 flex items-center justify-center text-slate-400 hover:bg-red-600 hover:text-white transition-colors"
        >
          <X size={14} />
        </button>
      </div>
    </header>
  );
}
