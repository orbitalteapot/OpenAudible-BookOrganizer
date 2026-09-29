import { useState } from 'react';
import { Square } from 'lucide-react';
import { formatDateTime, RUN_COUNTS, summariseRun } from '../../format';
import { isRunning } from '../../hooks';
import Button from '../ui/Button';
import { Banner, Card, ProgressBar, Stat } from '../ui/Surface';
import ProblemsList from './ProblemsList';

const COUNT_TONES = {
  new: 'positive',
  updated: 'accent',
  moved: 'accent',
  upToDate: 'default',
  notFound: 'caution',
  failed: 'critical',
};

/** "Sorting…", "Automatic sort", "Last sort", with when it started. */
function describeRun(status) {
  const scheduled = status.trigger === 'scheduled';
  const what = isRunning(status)
    ? scheduled
      ? 'Automatic sort running'
      : 'Sorting…'
    : scheduled
      ? 'Last automatic sort'
      : 'Last sort';

  return status.startedUtc ? `${what} · started ${formatDateTime(status.startedUtc)}` : what;
}

/** The outcome of a finished run, in the words every other place uses for it. */
function RunResult({ summary: { headline, details, tone } }) {
  return (
    <Banner tone={tone}>
      <span className="block">{headline}</span>
      {details.map((detail) => (
        <span key={detail} className="mt-1 block">
          {detail}
        </span>
      ))}
    </Banner>
  );
}

/**
 * The current or last sort, however it was started. Follows the backend's run status, so a sort the
 * schedule started shows here with its progress and a Cancel button like any other.
 *
 * `lostContact` is set when the backend stopped answering.
 */
export default function ProgressCard({ status, cancel, lostContact }) {
  const [cancelError, setCancelError] = useState(null);
  const [canceling, setCanceling] = useState(false);

  if (!status || status.state === 'idle') {
    return (
      <Card title="Progress">
        <p className="py-8 text-center text-sm text-fg-subtle">Progress will appear here once a sort starts.</p>
        {lostContact && <Banner tone="critical">{lostContact}</Banner>}
      </Card>
    );
  }

  const running = isRunning(status);
  const counts = status.counts ?? {};
  const percentage = status.percentage || 0;
  const summary = running ? null : summariseRun(status);
  const phase = running ? 'Sorting' : status.isCanceled ? 'Canceled' : status.error ? 'Failed' : 'Finished';

  const handleCancel = async () => {
    setCanceling(true);
    setCancelError(null);
    try {
      await cancel();
    } catch (err) {
      setCancelError(err.message);
    } finally {
      setCanceling(false);
    }
  };

  return (
    <Card
      title="Progress"
      description={describeRun(status)}
      actions={
        running && (
          <Button icon={Square} loading={canceling} onClick={handleCancel}>
            Cancel
          </Button>
        )
      }
    >
      <div className="space-y-5">
        <div>
          <div className="mb-2 flex items-baseline justify-between">
            <span className="text-sm text-fg-muted">{phase}</span>
            <span className="tabular text-sm font-medium text-fg">{percentage.toFixed(0)}%</span>
          </div>
          <ProgressBar
            value={percentage}
            tone={summary?.tone ?? 'accent'}
            label={`Sort progress: ${phase}`}
          />
          {status.totalBooks > 0 && (
            <p className="tabular mt-2 text-2xs text-fg-subtle">
              {(status.currentBook || 0).toLocaleString()} of {status.totalBooks.toLocaleString()} books
            </p>
          )}
        </div>

        {/* Every book lands in exactly one of these, so they add up to the books processed. */}
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          {RUN_COUNTS.map(({ key, label }) => (
            <Stat key={key} label={label} value={counts[key] || 0} tone={COUNT_TONES[key]} />
          ))}
        </div>

        {running && status.currentTitle && (
          <div className="rounded border border-line bg-raised px-3.5 py-3">
            <p className="mb-1 text-2xs text-fg-subtle">Current book</p>
            <p className="break-words text-sm text-fg-muted">{status.currentTitle}</p>
          </div>
        )}

        {summary && <RunResult summary={summary} />}

        <ProblemsList problems={status.problems} problemCount={status.problemCount} running={running} />

        {cancelError && <Banner tone="critical">Couldn&apos;t cancel: {cancelError}</Banner>}
        {lostContact && <Banner tone="critical">{lostContact}</Banner>}
      </div>
    </Card>
  );
}
