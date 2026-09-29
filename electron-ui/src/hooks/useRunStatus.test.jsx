import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getRunStatus, startSort } from '../api';
import { idleStatus, runningStatus } from '../test/fixtures';
import useRunStatus, { EXPECT_RUN_MS, IDLE_POLL_MS, LOST_CONTACT_MS, RUNNING_POLL_MS } from './useRunStatus';

vi.mock('../api', async (importOriginal) => ({
  ...(await importOriginal()),
  getRunStatus: vi.fn(),
  startSort: vi.fn(),
  cancelSort: vi.fn(),
}));

/** Lets due timers fire and the requests they start settle. */
const advance = (ms) => act(() => vi.advanceTimersByTimeAsync(ms));

describe('useRunStatus', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.mocked(getRunStatus).mockReset().mockResolvedValue(idleStatus());
    vi.mocked(startSort).mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('polls slowly while idle and quickly while a sort runs', async () => {
    const { result } = renderHook(() => useRunStatus());
    await advance(0);
    expect(getRunStatus).toHaveBeenCalledTimes(1);
    expect(result.current.status.state).toBe('idle');

    // Idle: nothing at the running cadence, the next poll only after the idle one.
    await advance(RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(1);

    // The schedule starts a run in the meantime; the idle poll finds it...
    vi.mocked(getRunStatus).mockResolvedValue(runningStatus({ trigger: 'scheduled' }));
    await advance(IDLE_POLL_MS - RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(2);
    expect(result.current.status.trigger).toBe('scheduled');

    // ...and from then on it is followed closely.
    await advance(RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(3);

    // Once it finishes, back to the idle cadence.
    vi.mocked(getRunStatus).mockResolvedValue({ ...runningStatus(), state: 'finished' });
    await advance(RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(4);
    await advance(RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(4);
  });

  it('watches closely for a while when a run is expected to start on its own', async () => {
    const { result } = renderHook(() => useRunStatus());
    await advance(0);
    expect(getRunStatus).toHaveBeenCalledTimes(1);

    // A schedule was just turned on: asked at once, then at the running cadence while idle...
    act(() => result.current.expectRun());
    await advance(0);
    expect(getRunStatus).toHaveBeenCalledTimes(2);
    await advance(RUNNING_POLL_MS);
    expect(getRunStatus).toHaveBeenCalledTimes(3);

    // ...but only for a while: no run turned up, so it goes back to the idle cadence.
    await advance(EXPECT_RUN_MS);
    const calls = vi.mocked(getRunStatus).mock.calls.length;
    await advance(RUNNING_POLL_MS * 2);
    expect(getRunStatus).toHaveBeenCalledTimes(calls);
  });

  it('follows the sort already running when the start is refused with 409', async () => {
    const { result } = renderHook(() => useRunStatus());
    await advance(0);

    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('A sort is already running.', { status: 409, code: 'alreadyRunning' })
    );
    vi.mocked(getRunStatus).mockResolvedValue(runningStatus({ trigger: 'scheduled' }));

    await act(() => result.current.start({ createDestination: false }));
    await advance(0);

    expect(startSort).toHaveBeenCalledWith({ createDestination: false });
    expect(result.current.status.state).toBe('running');
    expect(result.current.status.trigger).toBe('scheduled');
    expect(result.current.starting).toBe(false);
  });

  it('shows the run it started straight away', async () => {
    const { result } = renderHook(() => useRunStatus());
    await advance(0);

    vi.mocked(startSort).mockResolvedValueOnce(runningStatus({ currentBook: 0, percentage: 0 }));
    vi.mocked(getRunStatus).mockReturnValue(new Promise(() => {}));

    await act(() => result.current.start({}));

    expect(result.current.status.state).toBe('running');
  });

  it('stops showing a sort as running once contact is lost, in plain words', async () => {
    vi.mocked(getRunStatus).mockResolvedValue(runningStatus());
    const { result } = renderHook(() => useRunStatus());
    await advance(0);
    expect(result.current.status.state).toBe('running');

    // The backend died with the sort: "Sorting… 42%" and the disabled controls must not stay up.
    vi.mocked(getRunStatus).mockRejectedValue(new ApiError('unreachable', { code: 'unreachable' }));
    await advance(RUNNING_POLL_MS * 30);

    expect(result.current.status).toBeNull();
    expect(result.current.error).toBe(
      "Can't reach the organizer server. Check that the container is running, then reload this page."
    );
    expect(result.current.error).not.toMatch(/backend/);
  });

  it('reports lost contact within seconds when polls hang instead of failing at once', async () => {
    vi.mocked(getRunStatus).mockResolvedValueOnce(runningStatus());
    const { result } = renderHook(() => useRunStatus());
    await advance(0);
    expect(result.current.status.state).toBe('running');

    // A paused container or a dropped VPN: each poll fails only when its own timeout gives up.
    vi.mocked(getRunStatus).mockImplementation(
      () =>
        new Promise((_, reject) => {
          setTimeout(() => reject(new ApiError('The organizer did not answer in time.', { code: 'timeout' })), 5_000);
        })
    );
    await advance(LOST_CONTACT_MS.running + 6_000);

    expect(result.current.error).not.toBeNull();
    expect(result.current.status).toBeNull();
  });

  it('passes any other refusal on to the page', async () => {
    const { result } = renderHook(() => useRunStatus());
    await advance(0);

    const refusal = new ApiError('The destination folder does not exist. Is the drive connected?', {
      status: 400,
      code: 'destinationMissing',
      field: 'destinationPath',
    });
    vi.mocked(startSort).mockRejectedValueOnce(refusal);

    await expect(act(() => result.current.start({}))).rejects.toBe(refusal);
    expect(result.current.starting).toBe(false);
  });
});
