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

function formatTime(utc) {
  return new Date(utc).toLocaleString();
}

function describe(schedule, ready, isElectron) {
  if (schedule.managedByServer) {
    return `${formatInterval(schedule.intervalMinutes)}, set by the server's SORT_INTERVAL setting.`;
  }
  if (!ready) return 'Choose the three paths above to turn this on.';
  return isElectron
    ? 'Runs while the app is open. A run missed while it was closed happens when you next open it.'
    : 'Uses the paths and update check above.';
}

/**
 * Turns automatic sorting on or off. The backend runs the schedule with the paths and update
 * check chosen on this page.
 */
export default function ScheduleCard({ scheduleState, config, isElectron }) {
  const { schedule, error, saving, save } = scheduleState;
  if (!schedule) return null;

  const ready = Boolean(config.csvPath && config.sourcePath && config.destPath);
  const selected = schedule.intervalMinutes ? String(schedule.intervalMinutes) : OFF;

  return (
    <Card title="Automatic sorting">
      <div className="space-y-4">
        <Field label="Sort every" hint={describe(schedule, ready, isElectron)} group>
          {() => (
            <SegmentedControl
              label="Sort every"
              value={selected}
              options={INTERVALS}
              disabled={saving || schedule.managedByServer || !ready}
              onChange={(value) => save(value === OFF ? null : Number(value), config)}
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
