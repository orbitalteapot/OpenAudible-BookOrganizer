import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { counts, runningStatus } from '../../test/fixtures';
import { summariseRun } from '../../format';
import ProgressCard from '../sort/ProgressCard';

describe('ProgressCard', () => {
  it('says what a run is doing before its first book, instead of a frozen 0%', () => {
    const preparing = runningStatus({ preparing: true, totalBooks: 0, currentBook: 0, percentage: 0, currentTitle: null, counts: counts() });
    const { rerender } = render(<ProgressCard status={preparing} cancel={vi.fn()} />);

    expect(screen.getByText('Getting ready')).toBeTruthy();
    expect(screen.getByText(/Reading your library and checking the folders/)).toBeTruthy();
    expect(screen.queryByText('0%')).toBeNull();

    rerender(<ProgressCard status={runningStatus()} cancel={vi.fn()} />);
    expect(screen.getByText('Sorting')).toBeTruthy();
    expect(screen.getByText('42%')).toBeTruthy();
    expect(screen.queryByText(/Reading your library/)).toBeNull();
  });

  it('colours the bar by how the run went, not by books missing from the source', () => {
    const finished = (overrides) => ({ ...runningStatus(), state: 'finished', percentage: 100, ...overrides });
    const barFill = () => screen.getByRole('progressbar').firstChild.className;

    const { rerender } = render(<ProgressCard status={finished({ counts: counts({ new: 18, notFound: 1 }) })} cancel={vi.fn()} />);
    expect(barFill()).toContain('bg-positive');

    rerender(<ProgressCard status={finished({ counts: counts({ new: 17, failed: 1 }) })} cancel={vi.fn()} />);
    expect(barFill()).toContain('bg-critical');

    rerender(<ProgressCard status={finished({ isCanceled: true, counts: counts({ new: 3 }) })} cancel={vi.fn()} />);
    expect(barFill()).toContain('bg-caution');
  });

  it('keeps a failed Cancel to the run it was for', async () => {
    const cancel = vi.fn().mockRejectedValue(new Error('Could not reach the backend.'));
    const first = runningStatus({ startedUtc: '2026-09-29T10:00:00Z' });
    const { rerender } = render(<ProgressCard status={first} cancel={cancel} />);

    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(await screen.findByText(/Couldn't cancel: Could not reach the backend/)).toBeTruthy();

    // Still the same run: the error stays.
    rerender(<ProgressCard status={{ ...first, percentage: 60 }} cancel={cancel} />);
    expect(screen.getByText(/Couldn't cancel/)).toBeTruthy();

    // The next run, with the card still mounted, starts without it.
    rerender(<ProgressCard status={runningStatus({ startedUtc: '2026-09-29T11:00:00Z' })} cancel={cancel} />);
    expect(screen.queryByText(/Couldn't cancel/)).toBeNull();
  });

  it('keeps keyboard focus in the card when Cancel goes with the end of the run', async () => {
    const cancel = vi.fn().mockResolvedValue({});
    const status = runningStatus();
    const { rerender } = render(<ProgressCard status={status} cancel={cancel} />);

    const button = screen.getByRole('button', { name: 'Cancel' });
    button.focus();
    fireEvent.click(button);

    rerender(<ProgressCard status={{ ...status, state: 'finished', isCanceled: true, finishedUtc: new Date().toISOString() }} cancel={cancel} />);

    expect(screen.queryByRole('button', { name: 'Cancel' })).toBeNull();
    expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Progress' }));
  });

  it('shows a finished run without announcing it again, which the announcer did once already', () => {
    const finished = runningStatus({ state: 'finished', finishedUtc: new Date().toISOString(), counts: counts({ failed: 2 }) });
    render(<ProgressCard status={finished} cancel={vi.fn()} />);

    const result = screen.getByText(summariseRun(finished).headline).closest('.rounded');
    expect(result.getAttribute('role')).toBeNull();
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('says what the book it names is: the last one finished', () => {
    render(<ProgressCard status={runningStatus()} cancel={vi.fn()} />);

    expect(screen.getByText('Last finished')).toBeTruthy();
    expect(screen.queryByText('Current book')).toBeNull();
  });
});
