import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { getSchedule } from '../api';

/**
 * Automatic sorting: when the next sort is due and how the last one went.
 *
 * Its timing is a setting like any other, so it is changed through `useSettings`. Every settings
 * response (a save, or one of the app's regular refreshes) and every finished run moves the next
 * sort, so the schedule is asked for again whenever `settings` or `runFinishedUtc` changes. That
 * also keeps "Next sort: in 3 hours" and a retry after a failed run current without a page reload.
 *
 * `schedule` stays null until the backend has answered, which hides the card rather than showing
 * it empty.
 *
 * `error` says why the last change was refused. A refusal about a path ("The destination folder
 * does not exist") is the one `fieldErrors` holds for that path, shown until that one goes: a
 * different path is picked, or the folder turns up.
 */
export default function useSchedule({ settings, update, fieldErrors, runFinishedUtc }) {
  const [schedule, setSchedule] = useState(null);
  const [refusal, setRefusal] = useState(null);
  const [saving, setSaving] = useState(false);
  const latest = useRef(0);

  const refresh = useCallback(async () => {
    // Only the newest request may answer: a timer's request sent before a save must not land after
    // the save's own and show the schedule as it was.
    const id = ++latest.current;
    try {
      const next = await getSchedule();
      if (id === latest.current) setSchedule(next);
    } catch {
      // Keep showing the last schedule the backend reported; the next refresh tries again.
    }
  }, []);

  useEffect(() => {
    refresh();
  }, [refresh, settings, runFinishedUtc]);

  /**
   * Sorts every `minutes`, or turns automatic sorting off with null. Turning it off also turns off
   * running in the background and starting at sign-in: those only exist to keep the schedule going,
   * and their switches are hidden while it is off, so leaving them on would keep the app in the
   * tray with nothing on screen to explain why.
   */
  const changeInterval = useCallback(
    async (minutes) => {
      setSaving(true);
      setRefusal(null);
      const patch =
        minutes === null
          ? { scheduleIntervalMinutes: null, keepRunningInBackground: false, openAtLogin: false }
          : { scheduleIntervalMinutes: minutes };

      const refused = await update(patch);
      setSaving(false);
      if (refused) setRefusal({ field: refused.field, message: refused.message });
      return !refused;
    },
    [update]
  );

  const error = refusal?.field ? (fieldErrors?.[refusal.field] ?? null) : (refusal?.message ?? null);

  return useMemo(
    () => ({ schedule, error, saving, changeInterval }),
    [schedule, error, saving, changeInterval]
  );
}
