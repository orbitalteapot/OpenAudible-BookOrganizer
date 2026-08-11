import { useCallback, useEffect, useRef } from 'react';
import { cancelSort, getSortProgress, startSort } from '../api';

const POLL_INTERVAL_MS = 400;
const MAX_POLL_FAILURES = 25; // ~10 seconds of silence before we call the backend gone

/**
 * Drives one sort run: starting it, polling its progress, and cancelling it.
 *
 * The run state lives above this hook so that it survives switching pages — the panel unmounts,
 * the run does not — and so polling can pick straight back up when the panel comes back.
 */
export default function useSortRun({ run, setRun, config }) {
  const pollRef = useRef(null);
  // The interval callback is created once but reads the setter every tick; keeping it in a ref
  // avoids tearing the interval down and rebuilding it whenever the parent re-renders.
  const setRunRef = useRef(setRun);
  setRunRef.current = setRun;

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearInterval(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  const startPolling = useCallback(() => {
    stopPolling();

    // A single failed poll is normal — the backend is busy copying. A run of them means it is
    // gone, and leaving the UI spinning on "Sorting..." forever is worse than saying so.
    let consecutiveFailures = 0;

    pollRef.current = setInterval(async () => {
      try {
        const progress = await getSortProgress();
        consecutiveFailures = 0;

        if (progress.isComplete) {
          stopPolling();
          setRunRef.current((prev) => ({
            ...prev,
            progress,
            sorting: false,
            error: progress.error || prev.error,
          }));
          return;
        }

        setRunRef.current((prev) => ({ ...prev, progress }));
      } catch (err) {
        consecutiveFailures += 1;
        if (consecutiveFailures >= MAX_POLL_FAILURES) {
          stopPolling();
          setRunRef.current((prev) => ({
            ...prev,
            sorting: false,
            error: `Lost contact with the backend while sorting: ${err.message}`,
          }));
        }
      }
    }, POLL_INTERVAL_MS);
  }, [stopPolling]);

  // Resume polling when the panel is mounted while a run is still going.
  useEffect(() => {
    if (run.sorting && !pollRef.current) {
      startPolling();
    }

    return stopPolling;
  }, [run.sorting, startPolling, stopPolling]);

  const start = useCallback(async () => {
    const { csvPath, sourcePath, destPath, comparisonMode } = config;

    if (!csvPath || !sourcePath || !destPath) {
      setRunRef.current((prev) => ({ ...prev, error: 'All three paths are required' }));
      return;
    }

    setRunRef.current((prev) => ({ ...prev, error: null, sorting: true, progress: null }));

    try {
      await startSort(csvPath, sourcePath, destPath, comparisonMode);
      startPolling();
    } catch (err) {
      setRunRef.current((prev) => ({ ...prev, error: err.message, sorting: false }));
    }
  }, [config, startPolling]);

  const cancel = useCallback(async () => {
    try {
      await cancelSort();
      setRunRef.current((prev) => ({ ...prev, error: null }));
    } catch (err) {
      setRunRef.current((prev) => ({ ...prev, error: err.message }));
    }
  }, []);

  return { start, cancel };
}
