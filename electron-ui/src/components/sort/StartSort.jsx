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
 * path it was about.
 */
export default function StartSort({ settings, run, isElectron, error, onError }) {
  const [confirmCreate, setConfirmCreate] = useState(false);
  const createRef = useRef(null);
  const reasonId = useId();

  const active = isRunning(run.status);
  const reason = active ? 'A sort is already running.' : pathsBlockedReason(settings, isElectron);

  // Move to the question, so keyboard and screen-reader users are not left on a button that
  // appears to have done nothing.
  useEffect(() => {
    if (confirmCreate) createRef.current?.focus();
  }, [confirmCreate]);

  const begin = async (createDestination) => {
    setConfirmCreate(false);
    onError(null);
    try {
      await run.start({ createDestination });
    } catch (err) {
      if (err.code === 'destinationMissing') setConfirmCreate(true);
      else onError(err);
    }
  };

  return (
    <div className="space-y-3">
      {confirmCreate ? (
        <div className="space-y-3 rounded border border-caution/30 bg-caution/10 px-3.5 py-3">
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
