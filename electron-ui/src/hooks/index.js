import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { isDesktop } from '../mode';

export { default as useVirtualRows } from './useVirtualRows';
export { default as useFocusFallback } from './useFocusFallback';
export { default as useLibrary } from './useLibrary';
export { default as useRunStatus, isRunning } from './useRunStatus';
export { default as useSchedule } from './useSchedule';
export { default as useSettings } from './useSettings';
export { default as useTheme, THEMES } from './useTheme';

/**
 * Whether the app is running inside Electron rather than a browser tab. Electron-only affordances
 * (native file pickers, window controls) hang off this.
 */
export function useIsElectron() {
  // Read once: window.electronAPI is injected by the preload script before React mounts and never
  // changes afterwards, so re-checking on every render only invites inconsistent branches.
  const [isElectron] = useState(isDesktop);
  return isElectron;
}

/**
 * Why the desktop app has left its organizer stopped, with nothing left to start it again; null while
 * it runs or is being restarted, and always in a browser, where only the page can tell.
 */
export function useBackendStopped() {
  const [reason, setReason] = useState(null);

  useEffect(() => {
    const api = window.electronAPI;
    if (!api?.onBackendStopped) return undefined;

    let active = true;
    // It may have stopped before the page was loaded, or before this subscribed.
    api.backendStopped?.().then((current) => {
      if (active && current) setReason(current);
    });
    const unsubscribe = api.onBackendStopped(setReason);
    return () => {
      active = false;
      unsubscribe();
    };
  }, []);

  return reason;
}

/**
 * A ref that always holds the latest committed `value`, for code that runs later (after a request
 * comes back) and needs what is on screen then, not what was when it started.
 */
export function useLatest(value) {
  const ref = useRef(value);
  useEffect(() => {
    ref.current = value;
  });
  return ref;
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
