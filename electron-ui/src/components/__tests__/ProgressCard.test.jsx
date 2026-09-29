import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { counts, runningStatus } from '../../test/fixtures';
import { summariseRun } from '../../format';
import ProgressCard from '../sort/ProgressCard';

describe('ProgressCard', () => {
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
