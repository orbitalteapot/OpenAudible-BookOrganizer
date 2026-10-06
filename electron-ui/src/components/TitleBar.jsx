import { useEffect, useState } from 'react';
import { Minus, Power, Square, X, Copy } from 'lucide-react';

function WindowButton({ label, onClick, danger = false, children }) {
  return (
    <button
      type="button"
      onClick={onClick}
      aria-label={label}
      title={label}
      className={[
        'flex h-9 w-11 items-center justify-center text-fg-muted transition-colors',
        danger ? 'hover:bg-critical-solid hover:text-critical-solid-fg' : 'hover:bg-raised hover:text-fg',
      ].join(' ')}
    >
      {children}
    </button>
  );
}

/**
 * The desktop window's own title bar, since the window is frameless. Not shown in a browser.
 *
 * While closing the window keeps the app running, it also offers Quit: the tray icon that otherwise
 * does is missing on desktops without a tray (stock GNOME), where the app could not be quit at all.
 */
export default function TitleBar() {
  const [isMaximized, setIsMaximized] = useState(false);
  const [staysInBackground, setStaysInBackground] = useState(false);

  useEffect(() => {
    window.electronAPI?.staysInBackground?.().then((value) => setStaysInBackground(!!value));
    return window.electronAPI?.onStaysInBackgroundChanged?.((value) => setStaysInBackground(!!value));
  }, []);

  useEffect(() => {
    // Ask once on mount: the window can start maximised, and assuming otherwise shows the wrong
    // restore/maximise glyph until the user clicks it.
    window.electronAPI?.isMaximized().then((value) => setIsMaximized(!!value));

    // And keep listening, because the window can also be snapped or restored by the OS without
    // the button ever being pressed.
    return window.electronAPI?.onMaximizedChanged?.((value) => setIsMaximized(!!value));
  }, []);

  // The maximize/unmaximize event updates the glyph; this only asks for the change.
  const handleMaximize = () => window.electronAPI?.maximize();

  return (
    <header className="titlebar-drag flex h-9 shrink-0 select-none items-center justify-between border-b border-line bg-canvas pl-4">
      <span className="text-xs text-fg-subtle">OpenAudible Book Organizer</span>

      <div className="titlebar-no-drag flex items-center">
        {staysInBackground && (
          <WindowButton label="Quit Book Organizer" onClick={() => window.electronAPI?.quit?.()}>
            <Power size={14} aria-hidden="true" />
          </WindowButton>
        )}
        <WindowButton label="Minimise" onClick={() => window.electronAPI?.minimize()}>
          <Minus size={14} aria-hidden="true" />
        </WindowButton>
        <WindowButton label={isMaximized ? 'Restore' : 'Maximise'} onClick={handleMaximize}>
          {isMaximized ? <Copy size={12} aria-hidden="true" /> : <Square size={12} aria-hidden="true" />}
        </WindowButton>
        <WindowButton
          label={staysInBackground ? 'Close (keeps running in the background)' : 'Close'}
          onClick={() => window.electronAPI?.close()}
          danger
        >
          <X size={14} aria-hidden="true" />
        </WindowButton>
      </div>
    </header>
  );
}
