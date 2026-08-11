import { useEffect, useLayoutEffect, useRef, useState } from 'react';

export { default as useVirtualRows } from './useVirtualRows';
export { default as useSortRun } from './useSortRun';

/**
 * Whether the app is running inside Electron rather than a browser tab. Electron-only affordances
 * (native file pickers, window controls) hang off this.
 */
export function useIsElectron() {
  // Read once: window.electronAPI is injected by the preload script before React mounts and never
  // changes afterwards, so re-checking on every render only invites inconsistent branches.
  const [isElectron] = useState(() => typeof window !== 'undefined' && !!window.electronAPI);
  return isElectron;
}

/**
 * Delays a rapidly changing value. Filtering a large library on every keystroke re-runs the whole
 * filter-and-sort pass; waiting for a pause in typing keeps the field responsive.
 */
export function useDebounced(value, delay = 150) {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delay);
    return () => clearTimeout(timer);
  }, [value, delay]);

  return debounced;
}

/** The observed width of an element, for choosing what fits rather than guessing from the viewport. */
export function useElementWidth() {
  const ref = useRef(null);
  const [width, setWidth] = useState(0);

  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return undefined;

    setWidth(element.clientWidth);

    const observer = new ResizeObserver(([entry]) => setWidth(entry.contentRect.width));
    observer.observe(element);

    return () => observer.disconnect();
  }, []);

  return [ref, width];
}
