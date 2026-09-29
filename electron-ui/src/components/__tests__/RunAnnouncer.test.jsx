import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { counts, idleStatus } from '../../test/fixtures';
import RunAnnouncer from '../RunAnnouncer';

const finished = (finishedUtc) => ({
  ...idleStatus(),
  state: 'finished',
  trigger: 'manual',
  startedUtc: finishedUtc,
  finishedUtc,
  totalBooks: 3,
  currentBook: 3,
  percentage: 100,
  counts: counts({ upToDate: 3 }),
});

describe('RunAnnouncer', () => {
  it('announces every run that ends, even with the same words as the last', () => {
    const { container, rerender } = render(<RunAnnouncer status={idleStatus()} />);
    const region = container.querySelector('[aria-live="polite"]');

    rerender(<RunAnnouncer status={finished('2026-09-30T10:00:00Z')} />);
    const first = region.firstChild;
    expect(region.textContent).toBe('Sort complete: 3 up to date.');

    // Nothing changed in the library, so the second run ends the same way: still a change to the region.
    rerender(<RunAnnouncer status={finished('2026-09-30T11:00:00Z')} />);
    expect(region.textContent).toBe('Sort complete: 3 up to date.');
    expect(region.firstChild).not.toBe(first);
  });

  it('says nothing about a run that had finished before the app opened', () => {
    const { container } = render(<RunAnnouncer status={finished('2026-09-30T10:00:00Z')} />);

    expect(container.querySelector('[aria-live="polite"]').textContent).toBe('');
  });
});
