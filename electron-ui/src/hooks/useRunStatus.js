import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { cancelSort, getRunStatus, PROGRESS_TIMEOUT_MS, startSort } from '../api';
import { unreachableMessage } from '../mode';

// Fast enough for a progress bar to move smoothly while a sort runs; slow enough otherwise that
// watching for a run the schedule starts costs next to nothing.
export const RUNNING_POLL_MS = 400;
export const IDLE_POLL_MS = 3_000;
// How long to keep the fast cadence after being told a run is about to start on its own.
export const EXPECT_RUN_MS = 5_000;

// A failed poll or two is normal while the backend is busy copying; no answer for this long means it
// has gone. Counted in time rather than in failures, because a poll that hangs takes as long as its
// timeout to fail, not a moment. Longer while idle, where a container restarting is no news.
export const LOST_CONTACT_MS = { running: 10_000, idle: 30_000 };

export const isRunning = (status) => status?.state === 'running';

/**
 * The backend's current or most recent sort, whoever started it: this page, the schedule, or
 * another window. The backend is the only judge of whether a sort is running, so the page follows
 * what it reports rather than keeping its own idea of it.
 *
 * `start` resolves once the backend has accepted the run, and rejects with the ApiError it was
 * refused with. A sort that is already running is not a failure: the page just follows that one.
 */
export default function useRunStatus() {
  const [status, setStatus] = useState(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState(null);

  // Set by the polling effect: asks for the status now rather than at the next tick.
  const pollNow = useRef(() => {});
  // Until this time the idle cadence is the running one, see expectRun.
  const fastUntil = useRef(0);

  useEffect(() => {
    let active = true;
    let timer = null;
    // When the first poll of the current run of failures was sent; null while polls are answered.
    let failingSince = null;
    let running = false;
    // Polls can overlap when one is asked for early. Only the newest applies its answer and
    // schedules the next, so there is only ever one timer and an older reply never wins.
    let latest = 0;

    const poll = async () => {
      clearTimeout(timer);
      const id = ++latest;
      const sentAt = Date.now();
      try {
        const next = await getRunStatus();
        if (!active || id !== latest) return;
        failingSince = null;
        running = isRunning(next);
        setStatus(next);
        setError(null);
      } catch (err) {
        if (!active || id !== latest) return;
        // Timed from the first failed poll, not the last answer: after a laptop sleeps that answer is
        // an hour old, though the page has only just started trying again while the network comes
        // back. A poll that was under way when it went to sleep counts from no earlier than its
        // timeout allows, as no poll takes longer to fail.
        failingSince ??= Math.max(sentAt, Date.now() - PROGRESS_TIMEOUT_MS);
        const silentFor = Date.now() - failingSince;
        if (silentFor >= (running ? LOST_CONTACT_MS.running : LOST_CONTACT_MS.idle)) {
          setError(unreachableMessage());
        }
      }
      const fast = running || Date.now() < fastUntil.current;
      timer = setTimeout(poll, fast ? RUNNING_POLL_MS : IDLE_POLL_MS);
    };

    pollNow.current = poll;
    poll();

    return () => {
      active = false;
      clearTimeout(timer);
      pollNow.current = () => {};
    };
  }, []);

  const start = useCallback(async (options) => {
    setStarting(true);
    try {
      setStatus(await startSort(options));
    } catch (err) {
      if (err.code !== 'alreadyRunning') throw err;
    } finally {
      setStarting(false);
      // Picks up the faster cadence at once, and the run already going when the start was refused.
      pollNow.current();
    }
  }, []);

  /**
   * Says a sort may be about to start without this page starting it: the first run of a schedule
   * just turned on, or the catch-up run at launch. It is then picked up within a moment rather than
   * at the next idle poll, which left "Press Start sorting to begin" showing beside a running sort.
   */
  const expectRun = useCallback(() => {
    fastUntil.current = Date.now() + EXPECT_RUN_MS;
    pollNow.current();
  }, []);

  const cancel = useCallback(async () => {
    try {
      await cancelSort();
    } catch (err) {
      // A 400 means there was nothing left to cancel: the run finished first, as the next status shows.
      if (err.status !== 400) throw err;
    } finally {
      pollNow.current();
    }
  }, []);

  // Without contact, the last report of a running sort says nothing about now: the backend may have
  // died with it. Showing it would keep "Sorting… 42%" up and every control disabled for a run that
  // is no longer happening.
  const shown = error && isRunning(status) ? null : status;

  return useMemo(
    () => ({ status: shown, start, cancel, expectRun, starting, error }),
    [shown, start, cancel, expectRun, starting, error]
  );
}
