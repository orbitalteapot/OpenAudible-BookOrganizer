import { useEffect, useState } from 'react';
import { Minus, Square, X, Copy } from 'lucide-react';
import { useIsElectron } from '../hooks';

function WindowButton({ label, onClick, danger = false, children }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      title={label}
      className={[
        'flex h-9 w-11 items-center justify-center text-fg-muted transition-colors',
        danger ? 'hover:bg-critical hover:text-white' : 'hover:bg-raised hover:text-fg',
      ].join(' ')}
    >
      {children}
    </button>
  );
}

export default function TitleBar() {
  const isElectron = useIsElectron();
  const [isMaximized, setIsMaximized] = useState(false);

  useEffect(() => {
    if (!isElectron) return undefined;

    // Ask once on mount: the window can start maximised, and assuming otherwise shows the wrong
    // restore/maximise glyph until the user clicks it.
    window.electronAPI?.isMaximized().then((value) => setIsMaximized(!!value));

    // And keep listening, because the window can also be snapped or restored by the OS without
    // the button ever being pressed.
    return window.electronAPI?.onMaximizedChanged?.((value) => setIsMaximized(!!value));
  }, [isElectron]);

  // The maximize/unmaximize event updates the glyph; this only asks for the change.
  const handleMaximize = () => window.electronAPI?.maximize();

  return (
    <header
      className={[
        'flex h-9 shrink-0 select-none items-center justify-between border-b border-line bg-canvas pl-4',
        isElectron ? 'titlebar-drag' : 'pr-4',
      ].join(' ')}
    >
      <span className="text-xs text-fg-subtle">OpenAudible Book Organizer</span>

      {isElectron && (
        <div className="titlebar-no-drag flex items-center">
          <WindowButton label="Minimise" onClick={() => window.electronAPI?.minimize()}>
            <Minus size={14} aria-hidden="true" />
          </WindowButton>
          <WindowButton label={isMaximized ? 'Restore' : 'Maximise'} onClick={handleMaximize}>
            {isMaximized ? <Copy size={12} aria-hidden="true" /> : <Square size={12} aria-hidden="true" />}
          </WindowButton>
          <WindowButton label="Close" onClick={() => window.electronAPI?.close()} danger>
            <X size={14} aria-hidden="true" />
          </WindowButton>
        </div>
      )}
    </header>
  );
}
