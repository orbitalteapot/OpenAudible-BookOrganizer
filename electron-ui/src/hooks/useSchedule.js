import { useCallback, useEffect, useMemo, useState } from 'react';
import { getSchedule, saveSchedule } from '../api';

// Picks up the result of a scheduled run that finished while the app was open.
const REFRESH_INTERVAL_MS = 60_000;

/**
 * The automatic-sort schedule the backend runs. `schedule` stays null against an older backend
 * without scheduling, which hides the feature rather than showing an error.
 */
export default function useSchedule() {
  const [schedule, setSchedule] = useState(null);
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    let active = true;
    const refresh = () =>
      getSchedule()
        .then((next) => active && setSchedule(next))
        .catch(() => {});

    refresh();
    const timer = setInterval(refresh, REFRESH_INTERVAL_MS);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, []);

  /** Sorts every `intervalMinutes` (null for off) using the paths and update check in `config`. */
  const save = useCallback(async (intervalMinutes, config) => {
    setSaving(true);
    setError(null);
    try {
      setSchedule(
        await saveSchedule({
          intervalMinutes,
          csvPath: config.csvPath,
          sourcePath: config.sourcePath,
          destinationPath: config.destPath,
          comparisonMode: config.comparisonMode,
        })
      );
    } catch (err) {
      setError(err.message);
    } finally {
      setSaving(false);
    }
  }, []);

  return useMemo(() => ({ schedule, error, saving, save }), [schedule, error, saving, save]);
}
