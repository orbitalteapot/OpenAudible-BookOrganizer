import { useState } from 'react';
import { formatDateTime, formatInterval, formatRelative, summariseRun } from '../../format';
import { isRunning } from '../../hooks';
import { pathsBlockedReason } from '../../paths';
import { Field } from '../ui/Field';
import SegmentedControl from '../ui/SegmentedControl';
import { Banner, Card } from '../ui/Surface';
import Switch from '../ui/Switch';

const OFF = 'off';
const PRESETS = [
  { value: OFF, label: 'Off' },
  { value: '360', label: '6 hours' },
  { value: '720', label: '12 hours' },
  { value: '1440', label: 'Daily' },
  { value: '10080', label: 'Weekly' },
];

/** The presets, plus the server's own interval when SORT_INTERVAL set one that is not a preset. */
function intervalOptions(minutes) {
  if (!minutes || PRESETS.some(({ value }) => value === String(minutes))) return PRESETS;
  return [...PRESETS, { value: String(minutes), label: formatInterval(minutes) }];
}

/** When the next sort happens: "today at 21:14 (in 3 hours)", or "now" once it is due. */
function describeNextRun(nextRunUtc) {
  const now = new Date();
  if (new Date(nextRunUtc) <= now) return 'Now';
  return `${formatDateTime(nextRunUtc, now)} (${formatRelative(nextRunUtc, now)})`;
}

/**
 * Why the last automatic attempt did not sort the library. A slot that came due during a manual sort
 * is retried when that sort is cancelled, which leaves no error to quote ("Sort canceled after 88 books.").
 */
function describeFailedAttempt(lastRun) {
  if (lastRun?.error) return lastRun.error;
  return lastRun?.isCanceled ? summariseRun(lastRun).headline : 'unknown error';
}

function ScheduleFacts({ isOn, schedule, runStatus }) {
  const { nextRunUtc, lastRun, retrying } = schedule;
  const sortRunning = isRunning(runStatus);
  const runningNow = sortRunning && runStatus.trigger === 'scheduled';

  const facts = [];
  if (runningNow) {
    facts.push(['Now', 'An automatic sort is running.']);
  } else if (isOn && nextRunUtc && sortRunning && new Date(nextRunUtc) <= new Date()) {
    // A slot that comes due during a sort started by hand waits for that sort and counts as done
    // when it finishes: "Now" would promise a second sort that never comes.
    facts.push(['Next sort', 'Covered by the sort that is running.']);
  } else if (isOn && nextRunUtc) {
    facts.push(
      retrying
        ? ['Retrying', `${formatDateTime(nextRunUtc)} — last attempt failed: ${describeFailedAttempt(lastRun)}`]
        : ['Next sort', describeNextRun(nextRunUtc)]
    );
  }
  // A failed attempt is already described by the retry line.
  if (lastRun && !(retrying && !runningNow)) {
    facts.push(['Last sort', `${formatDateTime(lastRun.finishedUtc ?? lastRun.startedUtc)} — ${summariseRun(lastRun).headline}`]);
  }

  if (facts.length === 0) return null;

  return (
    <dl className="space-y-1 text-sm">
      {facts.map(([term, description]) => (
        <div key={term} className="flex gap-2">
          <dt className="w-20 shrink-0 text-fg-subtle">{term}</dt>
          <dd className="min-w-0 break-words text-fg-muted">{description}</dd>
        </div>
      ))}
    </dl>
  );
}

/**
 * Desktop only: whether automatic sorting carries on after the window is closed. Electron is told by
 * the app shell whenever these settings change.
 */
function BackgroundOptions({ settings, update, saving }) {
  return (
    <div className="space-y-3 border-t border-line pt-4">
      <Switch
        label="Keep running in the background when the window is closed"
        description="The app stays in the system tray and keeps sorting on schedule."
        checked={settings.keepRunningInBackground}
        disabled={saving}
        onChange={(value) => update({ keepRunningInBackground: value })}
      />
      <Switch
        label="Start when I sign in"
        description="Starts hidden in the tray, ready for the next sort."
        checked={settings.openAtLogin}
        disabled={saving}
        onChange={(value) => update({ openAtLogin: value })}
      />
      {!settings.keepRunningInBackground && (
        <p className="text-2xs text-fg-subtle">Sorts only while the app is open; a missed sort runs when you open it.</p>
      )}
    </div>
  );
}

/**
 * Turns automatic sorting on or off, and says when it runs and how it last went. Automatic sorts use
 * the same settings as Start sorting.
 */
export default function ScheduleCard({ scheduleState, settingsState, runStatus, isElectron }) {
  const { schedule, error, saving, changeInterval } = scheduleState;
  const { settings, update, saving: savingSettings } = settingsState;
  // Turning the schedule on starts a sort at once. Said until that first sort has finished, which
  // is when the schedule's last run changes from the one it had when it was turned on.
  const [firstRun, setFirstRun] = useState(null);

  if (!schedule || !settings) return null;

  // On or off, and how often, as the settings say: they change with the save's own reply, while the
  // schedule is asked for again after it and lags behind. Read from the schedule, the card showed
  // the old choice, with switches the new one hides, until that answer came.
  const intervalMinutes = settings.scheduleIntervalMinutes;
  const isOn = intervalMinutes != null;
  const lastRunKey = schedule.lastRun?.finishedUtc ?? null;
  const blocked = isOn ? null : pathsBlockedReason(settings, isElectron);

  const hint = schedule.locked
    ? `${formatInterval(intervalMinutes)}. Set by the server's SORT_INTERVAL setting.`
    : blocked ?? (isOn ? 'Uses the same folders and options as Start sorting.' : 'The first sort starts as soon as you turn this on.');

  const select = async (value) => {
    const minutes = value === OFF ? null : Number(value);
    // A sort already running does the first slot's job, so the backend skips that slot without a
    // run of its own and no first sort starts; the notice would then stay up for a whole interval.
    const firstSortStarts = !isOn && minutes !== null && !isRunning(runStatus);
    if ((await changeInterval(minutes)) && firstSortStarts) setFirstRun({ lastRunKey });
  };

  const showFirstRun = isOn && firstRun && firstRun.lastRunKey === lastRunKey;

  return (
    <Card title="Automatic sorting">
      <div className="space-y-4">
        <Field label="Sort every" hint={hint} group>
          {(_, hintId) => (
            <SegmentedControl
              label="Sort every"
              value={isOn ? String(intervalMinutes) : OFF}
              options={intervalOptions(intervalMinutes)}
              describedBy={hintId}
              disabled={saving || schedule.locked || Boolean(blocked)}
              // Choosing starts a sort, so looking through the options with the arrows must not.
              manualActivation
              onChange={select}
            />
          )}
        </Field>

        {showFirstRun && (
          <Banner tone="positive">
            The first sort starts now, then {formatInterval(intervalMinutes).toLowerCase()}.
          </Banner>
        )}

        <ScheduleFacts isOn={isOn} schedule={schedule} runStatus={runStatus} />

        {schedule.blockedReason && (
          <Banner tone="caution">Automatic sorting can&apos;t run: {schedule.blockedReason}</Banner>
        )}
        {error && <Banner tone="critical">{error}</Banner>}

        {isElectron && isOn && <BackgroundOptions settings={settings} update={update} saving={savingSettings} />}
      </div>
    </Card>
  );
}
