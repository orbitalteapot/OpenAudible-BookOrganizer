import { useEffect, useId, useRef, useState } from 'react';
import { FolderPlus, Play } from 'lucide-react';
import { isRunning } from '../../hooks';
import { pathsBlockedReason } from '../../paths';
import Button from '../ui/Button';
import { Banner } from '../ui/Surface';

/**
 * Start sorting, with the saved settings. A destination that does not exist is only created once
 * the user says so, because the likeliest reason it is missing is an unplugged drive, and sorting
 * onto the internal disk instead would quietly fill it.
 *
 * `error` / `onError` hold why the last start was refused, which the page also shows under the
 * path it was about. `onRefused` is told whenever the backend refuses a start.
 */
export default function StartSort({ settings, run, isElectron, error, onError, onRefused }) {
  const [confirmCreate, setConfirmCreate] = useState(false);
  const createRef = useRef(null);
  const startRef = useRef(null);
  const wasConfirming = useRef(false);
  const reasonId = useId();

  const active = isRunning(run.status);
  const reason = active ? 'A sort is already running.' : pathsBlockedReason(settings, isElectron);

  // Move to the question, so keyboard and screen-reader users are not left on a button that
  // appears to have done nothing; and back to Start sorting once it is answered, since the
  // question's buttons disappear with it and would drop focus to the top of the page.
  useEffect(() => {
    if (confirmCreate) createRef.current?.focus();
    else if (wasConfirming.current) startRef.current?.focus();
    wasConfirming.current = confirmCreate;
  }, [confirmCreate]);

  const begin = async (createDestination) => {
    setConfirmCreate(false);
    onError(null);
    try {
      await run.start({ createDestination });
    } catch (err) {
      // A refusal means the folders are not what the page last heard (a drive was unplugged or
      // plugged in since), so their statuses are asked for again rather than left saying "Found".
      onRefused();
      // A destination the server sets is a container mount, which the backend never creates: a
      // missing one is a mapping to fix, not a folder to make.
      if (err.code === 'destinationMissing' && !settings.locks?.paths) setConfirmCreate(true);
      else onError(err);
    }
  };

  return (
    <div className="space-y-3">
      {confirmCreate ? (
        <div className="space-y-3 rounded border border-caution/40 bg-caution/10 p-3">
          <p className="text-sm text-fg">The destination folder doesn&apos;t exist. Is the drive connected?</p>
          <div className="flex flex-wrap gap-2">
            <Button ref={createRef} variant="primary" icon={FolderPlus} onClick={() => begin(true)}>
              Create folder and sort
            </Button>
            <Button onClick={() => setConfirmCreate(false)}>Cancel</Button>
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
          onClick={() => begin(false)}
        >
          Start sorting
        </Button>
      )}

      {reason && !confirmCreate && (
        <p id={reasonId} className="text-2xs text-fg-subtle">
          {reason}
        </p>
      )}

      {error && <Banner tone="critical">Couldn&apos;t start the sort: {error.message}</Banner>}
    </div>
  );
}
