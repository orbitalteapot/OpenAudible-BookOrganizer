import { useCallback, useEffect, useState } from 'react';
import { getSchedule, saveSchedule } from '../api';
import { formatInterval } from '../format';
import { Field } from './ui/Field';
import SegmentedControl from './ui/SegmentedControl';
import { Banner, Card } from './ui/Surface';

const OFF = 'off';
const INTERVALS = [
  { value: OFF, label: 'Off' },
  { value: '360', label: '6 hours' },
  { value: '720', label: '12 hours' },
  { value: '1440', label: 'Daily' },
  { value: '10080', label: 'Weekly' },
];

// Picks up the result of a scheduled run that finished while the page was open.
const REFRESH_INTERVAL_MS = 60_000;

function formatTime(utc) {
  return utc ? new Date(utc).toLocaleString() : null;
}

/**
 * Turns automatic sorting on or off. The backend runs the schedule, using the paths and update
 * check chosen above; in the desktop app it runs while the app is open and catches up on the next
 * start if a run was missed.
 */
export default function ScheduleCard({ config, setConfig, isElectron }) {
  const [schedule, setSchedule] = useState(null);
  const [error, setError] = useState(null);
  const [saving, setSaving] = useState(false);

  const refresh = useCallback(async () => {
    try {
      setSchedule(await getSchedule());
    } catch {
      // An older backend without scheduling: the card stays hidden.
    }
  }, []);

  useEffect(() => {
    refresh();
    const timer = setInterval(refresh, REFRESH_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [refresh]);

  // The desktop app does not otherwise remember its paths, so reopen it on the ones last scheduled.
  useEffect(() => {
    if (!isElectron || !schedule?.csvPath) return;
    setConfig((prev) =>
      prev.csvPath || prev.sourcePath || prev.destPath
        ? prev
        : {
            ...prev,
            csvPath: schedule.csvPath,
            sourcePath: schedule.sourcePath,
            destPath: schedule.destinationPath,
            comparisonMode: schedule.comparisonMode,
          }
    );
  }, [isElectron, schedule?.csvPath, schedule?.sourcePath, schedule?.destinationPath, schedule?.comparisonMode, setConfig]);

  const save = useCallback(
    async (intervalMinutes) => {
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
    },
    [config.csvPath, config.sourcePath, config.destPath, config.comparisonMode]
  );

  // Keep a running schedule on the settings shown above, so what you see is what will run.
  const intervalMinutes = schedule?.intervalMinutes ?? null;
  const outOfDate =
    schedule &&
    !schedule.managedByServer &&
    intervalMinutes &&
    config.csvPath &&
    config.sourcePath &&
    config.destPath &&
    (schedule.csvPath !== config.csvPath ||
      schedule.sourcePath !== config.sourcePath ||
      schedule.destinationPath !== config.destPath ||
      schedule.comparisonMode !== config.comparisonMode);

  // Stops on an error rather than retrying the same failing save on every render.
  useEffect(() => {
    if (outOfDate && !saving && !error) save(intervalMinutes);
  }, [outOfDate, saving, error, save, intervalMinutes]);

  if (!schedule) return null;

  const ready = config.csvPath && config.sourcePath && config.destPath;
  const selected = intervalMinutes ? String(intervalMinutes) : OFF;
  const isPreset = INTERVALS.some((option) => option.value === selected);

  const hint = schedule.managedByServer
    ? `${formatInterval(intervalMinutes)}, set by the server's SORT_INTERVAL setting.`
    : !ready
      ? 'Choose the three paths above to turn this on.'
      : isElectron
        ? 'Runs while the app is open. A run missed while it was closed happens when you next open it.'
        : 'Uses the paths and update check above.';

  return (
    <Card title="Automatic sorting">
      <div className="space-y-4">
        <Field label="Sort every" hint={hint} group>
          {() => (
            <SegmentedControl
              label="Sort every"
              value={isPreset ? selected : null}
              options={INTERVALS}
              disabled={saving || schedule.managedByServer || !ready}
              onChange={(value) => save(value === OFF ? null : Number(value))}
            />
          )}
        </Field>

        {(schedule.nextRunUtc || schedule.lastRunUtc) && (
          <dl className="space-y-1 text-sm">
            {schedule.nextRunUtc && (
              <div className="flex gap-2">
                <dt className="w-20 shrink-0 text-fg-subtle">Next run</dt>
                <dd className="text-fg-muted">{formatTime(schedule.nextRunUtc)}</dd>
              </div>
            )}
            {schedule.lastRunUtc && (
              <div className="flex gap-2">
                <dt className="w-20 shrink-0 text-fg-subtle">Last run</dt>
                <dd className="min-w-0 break-words text-fg-muted">
                  {formatTime(schedule.lastRunUtc)} — {schedule.lastResult}
                </dd>
              </div>
            )}
          </dl>
        )}

        {error && <Banner tone="critical">{error}</Banner>}
      </div>
    </Card>
  );
}
