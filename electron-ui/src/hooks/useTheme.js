import { useCallback, useLayoutEffect, useMemo, useState } from 'react';

export const THEMES = ['system', 'light', 'dark'];
const STORAGE_KEY = 'oabo.theme';

// Storage can be missing or refuse access (a locked-down browser, a private window). The theme is
// only a display preference, so it falls back to following the system rather than failing.
function readStoredTheme() {
  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    return THEMES.includes(stored) ? stored : 'system';
  } catch {
    return 'system';
  }
}

function storeTheme(theme) {
  try {
    window.localStorage.setItem(STORAGE_KEY, theme);
  } catch {
    // Not remembered for next time, but still applied now.
  }
}

/**
 * The colour theme: 'system' follows the OS, 'light' and 'dark' override it. Kept per viewer in the
 * browser rather than on the server, since two people can look at one Docker server in different
 * rooms. Applied as `data-theme` on <html>, and passed to Electron so native parts (menus,
 * dialogs, the window background) match.
 */
export default function useTheme() {
  const [theme, setThemeState] = useState(readStoredTheme);

  // Before paint, so a reload in dark mode does not flash the other palette first.
  useLayoutEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.electronAPI?.setTheme?.(theme);
  }, [theme]);

  const setTheme = useCallback((next) => {
    if (!THEMES.includes(next)) return;
    storeTheme(next);
    setThemeState(next);
  }, []);

  return useMemo(() => ({ theme, setTheme }), [theme, setTheme]);
}
