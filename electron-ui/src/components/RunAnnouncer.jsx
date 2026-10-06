import { useEffect, useRef, useState } from 'react';
import { summariseRun } from '../format';

/**
 * Tells screen-reader users how a sort ended, whichever page they are on and whoever started it.
 *
 * Always mounted, so it is being observed before the text arrives: a live region that appears
 * together with its message is routinely missed. A run that had already finished when the app
 * opened is not news, so only one that ends while it is open is announced.
 */
export default function RunAnnouncer({ status }) {
  const [message, setMessage] = useState(null);
  const announced = useRef(undefined);

  useEffect(() => {
    if (!status) return;
    const finished = status.state === 'finished' ? status.finishedUtc : null;

    if (announced.current === undefined) {
      announced.current = finished;
    } else if (finished && finished !== announced.current) {
      announced.current = finished;
      setMessage({ run: finished, text: summariseRun(status).headline });
    }
  }, [status]);

  return (
    <p aria-live="polite" className="sr-only">
      {/* A new element for every run: sorting an unchanged library ends with the same words each
          time, and the same text set again changes nothing on the page, so was never read out. */}
      {message && <span key={message.run}>{message.text}</span>}
    </p>
  );
}
