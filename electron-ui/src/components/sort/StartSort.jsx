import { useEffect, useId, useRef, useState } from 'react';
import { FolderInput, FolderPlus, Play } from 'lucide-react';
import { isRunning } from '../../hooks';
import { pathsBlockedReason } from '../../paths';
import Button from '../ui/Button';
import { Banner } from '../ui/Surface';

/**
 * The questions a refused start can turn into, by the backend's error code: what to ask, the button
 * that answers yes, and what that answer sends. A destination that does not exist is only created,
 * and one that has lost the file earlier sorts left in it only sorted into, once the user says so:
 * the likeliest reason for either is a drive that is unplugged or not mounted, and sorting onto the
 * internal disk instead would quietly fill it (and, under a mount point, hide the copy).
 */
const QUESTIONS = {
  destinationMissing: {
    text: "The destination folder doesn't exist. Is the drive connected?",
    confirm: 'Create folder and sort',
    icon: FolderPlus,
    options: { createDestination: true },
  },
  destinationUnmounted: {
    text: 'The destination folder no longer holds the file earlier sorts left in it; the drive may not be mounted. Sort into it anyway?',
    confirm: 'Sort into it anyway',
    icon: FolderInput,
    options: { confirmUnmounted: true },
  },
};

/**
 * Start sorting, with the saved settings, asking first when the destination looks like a stand-in
 * for a missing drive (see QUESTIONS).
 *
 * `error` is why the last start was refused, which the page also shows under the path it was
 * about. `onStart` is told when a start is asked for, and `onRefused` whenever the backend refuses
 * one: with the error to show, or null when this asks a question about the destination instead.
 */
export default function StartSort({ settings, run, isElectron, error, onStart, onRefused }) {
  const [question, setQuestion] = useState(null);
  const asking = question !== null;
  const confirmRef = useRef(null);
  const startRef = useRef(null);
  const wasConfirming = useRef(false);
  const reasonId = useId();
  const questionId = useId();

  const active = isRunning(run.status);
  const reason = active ? 'A sort is already running.' : pathsBlockedReason(settings, isElectron);

  // Move to the question, so keyboard and screen-reader users are not left on a button that
  // appears to have done nothing; and back to Start sorting once it is answered, since the
  // question's buttons disappear with it and would drop focus to the top of the page.
  useEffect(() => {
    if (asking) confirmRef.current?.focus();
    else if (wasConfirming.current) startRef.current?.focus();
    wasConfirming.current = asking;
  }, [asking]);

  const begin = async (options) => {
    setQuestion(null);
    onStart();
    try {
      await run.start(options);
    } catch (err) {
      // A destination the server sets is a container mount, which the backend never creates: a
      // missing one is a mapping to fix, not a folder to make.
      const ask = err.code === 'destinationMissing' && settings.locks?.paths ? null : (QUESTIONS[err.code] ?? null);
      setQuestion(ask);
      onRefused(ask ? null : err);
    }
  };

  return (
    <div className="space-y-3">
      {asking ? (
        // Escape answers like Cancel, the key keyboard users expect to back out of a question.
        <div
          className="space-y-3 rounded border border-caution/40 bg-caution/10 p-3"
          onKeyDown={(e) => e.key === 'Escape' && setQuestion(null)}
        >
          <p id={questionId} className="text-sm text-fg">
            {question.text}
          </p>
          <div className="flex flex-wrap gap-2">
            {/* Focus lands here, so the button carries the question: otherwise a screen reader
                says only "Create folder and sort" and never why it is being asked. */}
            <Button
              ref={confirmRef}
              variant="primary"
              icon={question.icon}
              aria-describedby={questionId}
              onClick={() => begin(question.options)}
            >
              {question.confirm}
            </Button>
            <Button onClick={() => setQuestion(null)}>Cancel</Button>
          </div>
        </div>
      ) : (
        <Button
          ref={startRef}
          variant="primary"
          icon={Play}
          className="w-full"
          loading={run.starting}
          disabledReason={reason}
          reasonId={reasonId}
          onClick={() => begin({ createDestination: false })}
        >
          Start sorting
        </Button>
      )}

      {reason && !asking && (
        <p id={reasonId} className="text-2xs text-fg-subtle">
          {reason}
        </p>
      )}

      {error && <Banner tone="critical">Couldn&apos;t start the sort: {error.message}</Banner>}
    </div>
  );
}
