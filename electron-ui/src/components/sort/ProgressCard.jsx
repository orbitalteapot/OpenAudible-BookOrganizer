import { useEffect, useRef, useState } from 'react';
import { Square } from 'lucide-react';
import { formatDateTime, RUN_COUNTS, summariseRun } from '../../format';
import { isRunning, useFocusFallback } from '../../hooks';
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

/**
 * The bar says how the run itself went: it finished, was stopped, or failed. Books that were not
 * found are the export's business, and the result banner below already calls them out.
 */
function barTone(status, running) {
  if (running) return 'accent';
  if (status.error || status.counts?.failed > 0) return 'critical';
  return status.isCanceled ? 'caution' : 'positive';
}

/** The outcome of a finished run, in the words every other place uses for it. */
function RunResult({ summary: { headline, details, tone } }) {
  return (
    // Not a live region: the announcer reads the outcome once as it happens, and an alert here was
    // read again every time the Sort page was opened, as if the old result were news.
    <Banner tone={tone} live={false}>
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
 * schedule started shows here with its progress and a Cancel button like any other. (Losing contact
 * with the backend is shown above every page; see AppNotices.)
 *
 * `focusRequested` moves focus to the card's heading, which scrolls it into view, and `onFocused` is
 * then told.
 */
export default function ProgressCard({ status, cancel, focusRequested = false, onFocused }) {
  // Remembered with the run it belongs to: the card stays mounted between runs, and a failed Cancel
  // on one run says nothing about the next.
  const [cancelFailure, setCancelFailure] = useState(null);
  const [canceling, setCanceling] = useState(false);
  const cancelError = cancelFailure?.run === status?.startedUtc ? cancelFailure.message : null;

  const running = isRunning(status);
  // Cancel goes when the run ends, whether it was cancelled or finished on its own.
  const headingRef = useRef(null);
  const cancelFocus = useFocusFallback(!running, headingRef);

  useEffect(() => {
    if (!focusRequested) return;
    headingRef.current?.focus();
    onFocused?.();
  }, [focusRequested, onFocused]);

  if (!status || status.state === 'idle') {
    return (
      <Card title="Progress" titleRef={headingRef}>
        <p className="py-8 text-center text-sm text-fg-subtle">Press Start sorting to begin. Progress will appear here.</p>
      </Card>
    );
  }

  const counts = status.counts ?? {};
  const percentage = status.percentage || 0;
  const summary = running ? null : summariseRun(status);
  // Before its first book a run reads the export and looks through both folders, which on a large
  // library on a network drive takes minutes. A bare 0% all that time looked hung.
  const preparing = running && status.preparing;
  const phase = running
    ? preparing
      ? 'Getting ready'
      : 'Sorting'
    : status.isCanceled
      ? 'Canceled'
      : status.error
        ? 'Failed'
        : 'Finished';

  const handleCancel = async () => {
    setCanceling(true);
    setCancelFailure(null);
    try {
      await cancel();
    } catch (err) {
      setCancelFailure({ run: status.startedUtc, message: err.message });
    } finally {
      setCanceling(false);
    }
  };

  return (
    <Card
      title="Progress"
      titleRef={headingRef}
      description={describeRun(status)}
      actions={
        running && (
          <Button icon={Square} loading={canceling} onClick={handleCancel} {...cancelFocus}>
            Cancel
          </Button>
        )
      }
    >
      <div className="space-y-5">
        <div>
          <div className="mb-2 flex items-baseline justify-between">
            <span className="text-sm text-fg-muted">{phase}</span>
            {!preparing && <span className="tabular text-sm font-medium text-fg">{percentage.toFixed(0)}%</span>}
          </div>
          <ProgressBar
            value={percentage}
            tone={barTone(status, running)}
            label={`Sort progress: ${phase}`}
          />
          {preparing ? (
            <p className="mt-2 text-2xs text-fg-subtle">
              Reading your library and checking the folders… On a network drive this can take a few minutes.
            </p>
          ) : (
            status.totalBooks > 0 && (
              <p className="tabular mt-2 text-2xs text-fg-subtle">
                {(status.currentBook || 0).toLocaleString()} of {status.totalBooks.toLocaleString()} books
              </p>
            )
          )}
        </div>

        {/* Every book lands in exactly one of these, so they add up to the books processed. */}
        <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
          {RUN_COUNTS.map(({ key, label }) => (
            <Stat key={key} label={label} value={counts[key] || 0} tone={COUNT_TONES[key]} />
          ))}
        </div>

        {running && status.currentTitle && (
          <div className="rounded border border-line bg-raised p-3">
            {/* Reported as each book finishes, so that is what it is: with Gentle, the book being
                copied now can take minutes, and naming the finished one as current misled. */}
            <p className="mb-1 text-2xs text-fg-subtle">Last finished</p>
            <p className="break-words text-sm text-fg-muted">{status.currentTitle}</p>
          </div>
        )}

        {summary && <RunResult summary={summary} />}

        <ProblemsList problems={status.problems} problemCount={status.problemCount} running={running} />

        {cancelError && <Banner tone="critical">Couldn&apos;t cancel: {cancelError}</Banner>}
      </div>
    </Card>
  );
}
