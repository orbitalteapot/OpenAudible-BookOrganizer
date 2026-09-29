import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getSettings, updateSettings } from '../api';
import { isFoundAgain } from '../paths';

// The desktop backend is started alongside the window and takes a few seconds to answer; a Docker
// container may be restarting. Keep asking, and only call it an error once it has taken a while.
const LOAD_RETRY_MS = 1_000;
const LOAD_ATTEMPTS_BEFORE_ERROR = 10;

/**
 * Drops the errors a successful save has dealt with: those about a field it changed, and those
 * the same change caused (turning automatic sorting on is refused with an error about a path, and
 * turning it off again makes that error moot).
 */
function withoutResolved(fieldErrors, patch) {
  const touched = (key) => Object.prototype.hasOwnProperty.call(patch, key);
  return Object.fromEntries(
    Object.entries(fieldErrors).filter(([field, { causes }]) => !touched(field) && !causes.some(touched))
  );
}

/**
 * Drops the errors about a path the backend now finds (see isFoundAgain), unless the error was about
 * changing that path: turning automatic sorting on with an unplugged drive is refused with an error
 * about the destination, which is moot once the drive is back, but a refused pick of a new folder
 * says nothing about the saved one being found.
 */
function withoutFound(fieldErrors, pathStatus) {
  return Object.fromEntries(
    Object.entries(fieldErrors).filter(
      ([field, { code, causes }]) => causes.includes(field) || !isFoundAgain({ field, code }, pathStatus)
    )
  );
}

/**
 * The app's settings, as the backend holds them.
 *
 * What is shown is always what the backend last said: a change is sent, and the page moves on only
 * when the reply comes back, so a refused change never leaves the page showing something the
 * backend is not doing. Saves and refreshes go out one at a time, in order, so a slow reply can
 * never land after a newer one and put an old value back.
 *
 * `fieldErrors` maps a setting ("destinationPath") to the message the backend refused it with, for
 * showing under that setting; `error` holds anything not about one setting.
 */
export default function useSettings() {
  const [settings, setSettings] = useState(null);
  // Kept with its code so a refresh can tell "the backend did not answer" (moot once it answers
  // again) from a save the backend refused (still true until a save succeeds).
  const [errorState, setErrorState] = useState(null);
  const [fieldErrorState, setFieldErrorState] = useState({});
  const [pending, setPending] = useState(0);

  const queue = useRef(Promise.resolve());
  // A refresh waiting for its turn, until it starts.
  const queuedRefresh = useRef(null);

  /** Runs `task` after every request queued before it. */
  const enqueue = useCallback((task) => {
    const next = queue.current.then(task);
    // A failure is handled inside the task; the queue itself must keep going.
    queue.current = next.catch(() => {});
    return next;
  }, []);

  useEffect(() => {
    // Cleared on unmount, so a remount (StrictMode does one) stops this retry loop instead of
    // leaving it running beside the new one.
    let active = true;
    let timer = null;
    let attempts = 0;

    const load = () =>
      enqueue(async () => {
        try {
          const loaded = await getSettings();
          if (!active) return;
          setSettings(loaded);
          setErrorState(null);
        } catch (err) {
          if (!active) return;
          attempts += 1;
          if (attempts >= LOAD_ATTEMPTS_BEFORE_ERROR) setErrorState(err);
          timer = setTimeout(load, LOAD_RETRY_MS);
        }
      });

    load();
    return () => {
      active = false;
      clearTimeout(timer);
    };
  }, [enqueue]);

  /**
   * Saves `patch` (wire names: `{ destinationPath }`). Resolves to null once saved, or to the
   * ApiError it was refused with, which is also recorded under its field.
   */
  const update = useCallback(
    (patch) => {
      setPending((count) => count + 1);

      return enqueue(async () => {
        try {
          const saved = await updateSettings(patch);
          setSettings(saved);
          setErrorState(null);
          setFieldErrorState((current) => withoutResolved(current, patch));
          return null;
        } catch (err) {
          if (err.field) {
            setFieldErrorState((current) => ({
              ...current,
              [err.field]: { message: err.message, code: err.code, causes: Object.keys(patch) },
            }));
          } else {
            setErrorState(err);
          }
          return err;
        } finally {
          setPending((count) => count - 1);
        }
      });
    },
    [enqueue]
  );

  /**
   * Asks again, for what only the backend can see: whether a folder has appeared since (a run
   * created it, a drive was plugged in). Queued behind any save, so it cannot undo one.
   *
   * A refresh asked for while another is still waiting for its turn is that one: it will fetch
   * the same thing. Otherwise every timer tick, focus and tab switch would queue one more while
   * the backend takes its time over an offline network share, and a save would wait behind all of them.
   */
  const refresh = useCallback(() => {
    if (queuedRefresh.current) return queuedRefresh.current;

    const refreshing = enqueue(async () => {
      queuedRefresh.current = null;
      try {
        const current = await getSettings();
        setSettings(current);
        setFieldErrorState((errors) => withoutFound(errors, current.pathStatus));
        setErrorState((shown) => (shown?.code === 'unreachable' ? null : shown));
      } catch {
        // The settings on screen are still the last ones the backend confirmed.
      }
    });
    queuedRefresh.current = refreshing;
    return refreshing;
  }, [enqueue]);

  const error = errorState?.message ?? null;

  const fieldErrors = useMemo(
    () => Object.fromEntries(Object.entries(fieldErrorState).map(([field, { message }]) => [field, message])),
    [fieldErrorState]
  );

  return useMemo(
    () => ({ settings, update, refresh, saving: pending > 0, error, fieldErrors }),
    [settings, update, refresh, pending, error, fieldErrors]
  );
}
