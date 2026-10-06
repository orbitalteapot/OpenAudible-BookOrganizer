import Disclosure from '../ui/Disclosure';

// The worst first: a failure needs acting on, a missing file usually just means not downloaded yet.
const GROUPS = [
  { kind: 'failed', label: 'Could not be copied' },
  { kind: 'notFound', label: 'No file in the source folder' },
  { kind: 'warning', label: 'Warnings' },
];

/**
 * The books a run had trouble with, and why. While a run is going the backend sends only the most
 * recent few; the full list (up to the backend's cap) arrives when it finishes.
 */
export default function ProblemsList({ problems = [], problemCount = 0, running = false }) {
  if (problemCount === 0) return null;

  const more = problemCount - problems.length;

  return (
    <Disclosure summary={`Problems (${problemCount.toLocaleString()})`}>
      <div className="space-y-3">
        {running && more > 0 && <p className="text-2xs text-fg-subtle">The latest {problems.length} so far:</p>}

        {GROUPS.map(({ kind, label }) => {
          const items = problems.filter((problem) => problem.kind === kind);
          if (items.length === 0) return null;

          return (
            <section key={kind}>
              <h3 className="mb-1 text-xs font-medium text-fg-muted">{label}</h3>
              <ul className="space-y-2">
                {items.map((problem, index) => (
                  <li key={index} className="text-sm">
                    <span className="block break-words text-fg">{problem.book}</span>
                    <span className="block break-words text-2xs text-fg-subtle">{problem.message}</span>
                  </li>
                ))}
              </ul>
            </section>
          );
        })}

        {!running && more > 0 && <p className="text-2xs text-fg-subtle">…and {more.toLocaleString()} more.</p>}
      </div>
    </Disclosure>
  );
}
