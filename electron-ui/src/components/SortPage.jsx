import { useCallback, useEffect, useMemo, useState } from 'react';
import { isUnanswered } from '../api';
import { isRunning, useLatest } from '../hooks';
import { isFoundAgain } from '../paths';
import FoldersCard from './sort/FoldersCard';
import OptionsCard from './sort/OptionsCard';
import ProgressCard from './sort/ProgressCard';
import ScheduleCard from './sort/ScheduleCard';
import StartSort from './sort/StartSort';
import { Banner, Card } from './ui/Surface';

/**
 * Where sorting is set up, started and followed. Everything on it reads from the backend: the
 * settings, the run in progress (whoever started it) and the schedule.
 *
 * `focusProgress` asks for the Progress card to take focus, and `onProgressFocused` says it has.
 */
export default function SortPage({ settingsState, run, scheduleState, isElectron, focusProgress, onProgressFocused }) {
  const { settings, update, refresh, saving, fieldErrors, error: settingsError, errorCode } = settingsState;
  // The last refused start (`error`), whether the backend has looked at the folders since
  // (`checked`), and the settings on screen when it was refused (`settingsThen`). Until the backend
  // has looked, the statuses on screen are from before the refusal, up to 30 seconds old, and can
  // still say "Found" about the drive that was just unplugged.
  const [refusal, setRefusal] = useState(null);
  const startError = refusal?.error ?? null;
  const runActive = isRunning(run.status);
  const latestSettings = useLatest(settings);

  // A start refused because the drive was unplugged goes once the backend finds the folder again,
  // by the same rule as a refused save's error about it. One that got no answer goes once the
  // backend answers again (new settings), or shows the sort it started after all: it may have
  // started, and "Couldn't start the sort" beside it, long after, says the opposite.
  const pathStatus = settings?.pathStatus;
  useEffect(() => {
    if (!refusal) return;
    const answered = isUnanswered(refusal.error) && (runActive || settings !== refusal.settingsThen);
    if (answered || (refusal.checked && isFoundAgain(refusal.error, pathStatus))) setRefusal(null);
  }, [refusal, settings, pathStatus, runActive]);

  // A refusal means the folders are not what the page last heard, so they are asked about again,
  // and the refusal can be answered by what comes back. `error` is null when the refusal is shown
  // some other way (the question whether to create the destination).
  const handleRefused = useCallback(
    (error) => {
      const refused = error && { error, checked: false, settingsThen: latestSettings.current };
      setRefusal(refused);
      refresh().then((answered) => {
        if (answered && refused) setRefusal((shown) => (shown === refused ? { ...refused, checked: true } : shown));
      });
    },
    [refresh, latestSettings]
  );
  const handleStart = useCallback(() => setRefusal(null), []);

  // A start refused because of one path is shown under that path too, until a newer save speaks
  // for it, a different path is picked there, or the folder turns up.
  const pathErrors = useMemo(
    () => ({ ...(startError?.field ? { [startError.field]: startError.message } : {}), ...fieldErrors }),
    [startError, fieldErrors]
  );

  const handlePicked = useCallback(
    (field) => setRefusal((current) => (current?.error.field === field ? null : current)),
    []
  );

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-4">
      <div>
        <h1 className="text-xl font-semibold tracking-tight text-fg">Sort</h1>
        <p className="text-xs text-fg-muted">
          Copies your books into Author / Series / Book folders, one folder per book. The source folder is never changed.
        </p>
      </div>

      {/* Not the organizer being out of reach while the app's own notice already says so. */}
      {settingsError && !(run.error && errorCode === 'unreachable') && <Banner tone="critical">{settingsError}</Banner>}

      <div className="grid min-h-0 flex-1 auto-rows-min grid-cols-1 items-start gap-4 overflow-y-auto xl:grid-cols-2">
        <div className="flex flex-col gap-4">
          <FoldersCard
            settings={settings}
            update={update}
            errors={pathErrors}
            runActive={runActive}
            isElectron={isElectron}
            onPicked={handlePicked}
          />
          <OptionsCard
            settings={settings}
            update={update}
            saving={saving}
            fieldErrors={fieldErrors}
            runActive={runActive}
          />
        </div>

        <div className="flex flex-col gap-4">
          <ProgressCard
            status={run.status}
            cancel={run.cancel}
            focusRequested={focusProgress}
            onFocused={onProgressFocused}
          />
          <ScheduleCard
            scheduleState={scheduleState}
            settingsState={settingsState}
            runStatus={run.status}
            isElectron={isElectron}
          />
        </div>
      </div>

      {/* Outside the scrolling cards, so it is always on screen: below them it sat under the fold
          at every window size, with nothing to say it was there. */}
      <Card>
        <StartSort
          settings={settings}
          run={run}
          isElectron={isElectron}
          error={run.error && startError?.code === 'unreachable' ? null : startError}
          onStart={handleStart}
          onRefused={handleRefused}
        />
      </Card>
    </div>
  );
}
