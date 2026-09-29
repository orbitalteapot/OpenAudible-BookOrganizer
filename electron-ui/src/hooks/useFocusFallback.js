import { useEffect, useMemo, useRef } from 'react';

/**
 * Keeps keyboard focus from falling to the top of the page when the control that has it is taken
 * away: Cancel once the run ends, "Choose export…" once the books it loaded replace the empty page.
 * Without this the next Tab starts again from the title bar, and a screen reader is left nowhere.
 *
 * Spread the returned props onto the control that may disappear. When `gone` turns true while it
 * had focus, focus moves to `targetRef`.
 */
export default function useFocusFallback(gone, targetRef) {
  const hadFocus = useRef(false);

  useEffect(() => {
    if (!gone || !hadFocus.current) return;
    hadFocus.current = false;
    // Only when focus really was dropped; someone who has clicked elsewhere keeps their place.
    const active = document.activeElement;
    if (!active || active === document.body) targetRef.current?.focus();
  }, [gone, targetRef]);

  return useMemo(
    () => ({
      onFocus: () => {
        hadFocus.current = true;
      },
      // A control being removed may report a blur with nowhere to go; only moving on to another
      // element means focus was taken away on purpose.
      onBlur: (event) => {
        if (event.relatedTarget) hadFocus.current = false;
      },
    }),
    []
  );
}
