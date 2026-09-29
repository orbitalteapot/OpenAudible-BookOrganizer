import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { runningStatus } from '../../test/fixtures';
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
});
