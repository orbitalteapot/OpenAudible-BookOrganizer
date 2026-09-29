import { useCallback, useEffect, useMemo, useState } from 'react';
import { isRunning } from '../hooks';
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
  const { settings, update, refresh, saving, fieldErrors, error: settingsError } = settingsState;
  const [startError, setStartError] = useState(null);
  const runActive = isRunning(run.status);

  // A start refused because the drive was unplugged goes once the backend finds the folder again,
  // by the same rule as a refused save's error about it.
  const pathStatus = settings?.pathStatus;
  useEffect(() => {
    if (startError && isFoundAgain(startError, pathStatus)) setStartError(null);
  }, [startError, pathStatus]);

  // A start refused because of one path is shown under that path too, until a newer save speaks
  // for it, a different path is picked there, or the folder turns up.
  const pathErrors = useMemo(
    () => ({ ...(startError?.field ? { [startError.field]: startError.message } : {}), ...fieldErrors }),
    [startError, fieldErrors]
  );

  const handlePicked = useCallback(
    (field) => setStartError((current) => (current?.field === field ? null : current)),
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

      {settingsError && <Banner tone="critical">{settingsError}</Banner>}

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
            lostContact={run.error}
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
          error={startError}
          onError={setStartError}
          onRefused={refresh}
        />
      </Card>
    </div>
  );
}
