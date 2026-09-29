import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getSettings, updateSettings } from '../api';
import { settingsResponse } from '../test/fixtures';
import useSettings from './useSettings';

vi.mock('../api', async (importOriginal) => ({
  ...(await importOriginal()),
  getSettings: vi.fn(),
  updateSettings: vi.fn(),
}));

/** A promise the test settles when it chooses, to hold a request in flight. */
function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

async function renderLoaded() {
  const hook = renderHook(() => useSettings());
  await waitFor(() => expect(hook.result.current.settings).not.toBeNull());
  return hook;
}

describe('useSettings', () => {
  beforeEach(() => {
    vi.mocked(getSettings).mockReset().mockResolvedValue(settingsResponse());
    vi.mocked(updateSettings).mockReset();
  });

  it('loads the settings on mount', async () => {
    const { result } = await renderLoaded();

    expect(result.current.settings.csvPath).toBe('/books/library.csv');
    expect(result.current.saving).toBe(false);
  });

  it('keeps asking while the backend is starting', async () => {
    vi.mocked(getSettings)
      .mockRejectedValueOnce(new ApiError('Could not reach the backend.', { code: 'unreachable' }))
      .mockResolvedValueOnce(settingsResponse());

    const { result } = renderHook(() => useSettings());

    await waitFor(() => expect(result.current.settings).not.toBeNull(), { timeout: 3000 });
    expect(getSettings).toHaveBeenCalledTimes(2);
    expect(result.current.error).toBeNull();
  });

  it('drops an error about a folder once a refresh finds it, but not a refused pick of a new one', async () => {
    const { result } = await renderLoaded();
    const unplugged = settingsResponse({ pathStatus: { csv: 'ok', source: 'ok', destination: 'notFound' } });
    vi.mocked(getSettings).mockResolvedValue(unplugged);
    await act(() => result.current.refresh());

    // Turning automatic sorting on is refused because of the destination...
    vi.mocked(updateSettings).mockRejectedValueOnce(
      new ApiError('The destination folder does not exist. Is the drive connected?', { status: 400, field: 'destinationPath' })
    );
    await act(() => result.current.update({ scheduleIntervalMinutes: 1440 }));
    // ...and a new source folder is refused on its own account.
    vi.mocked(updateSettings).mockRejectedValueOnce(
      new ApiError('The source folder does not exist.', { status: 400, field: 'sourcePath' })
    );
    await act(() => result.current.update({ sourcePath: '/elsewhere' }));
    expect(Object.keys(result.current.fieldErrors).sort()).toEqual(['destinationPath', 'sourcePath']);

    // The drive is plugged back in.
    vi.mocked(getSettings).mockResolvedValue(settingsResponse());
    await act(() => result.current.refresh());

    expect(result.current.fieldErrors).toEqual({ sourcePath: 'The source folder does not exist.' });
  });

  it('sends one save at a time, in order, and shows what the backend saved', async () => {
    const { result } = await renderLoaded();
    const first = deferred();
    const second = deferred();
    vi.mocked(updateSettings).mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);

    let saves;
    act(() => {
      saves = [result.current.update({ copySpeed: 'gentle' }), result.current.update({ comparisonMode: 'full' })];
    });

    // The second waits for the first, and nothing changes on screen until the backend answers.
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(1));
    expect(updateSettings).toHaveBeenLastCalledWith({ copySpeed: 'gentle' });
    expect(result.current.saving).toBe(true);
    expect(result.current.settings.copySpeed).toBe('normal');

    await act(async () => first.resolve(settingsResponse({ copySpeed: 'gentle' })));
    await waitFor(() => expect(updateSettings).toHaveBeenCalledTimes(2));
    expect(updateSettings).toHaveBeenLastCalledWith({ comparisonMode: 'full' });
    expect(result.current.settings.copySpeed).toBe('gentle');

    await act(async () => second.resolve(settingsResponse({ copySpeed: 'gentle', comparisonMode: 'full' })));
    await expect(Promise.all(saves)).resolves.toEqual([null, null]);
    expect(result.current.settings.comparisonMode).toBe('full');
    expect(result.current.saving).toBe(false);
  });

  it('keeps the saved value and records the error under its field when a save is refused', async () => {
    const { result } = await renderLoaded();
    const refusal = new ApiError('The destination folder cannot be the source folder or a folder inside it.', {
      status: 400,
      field: 'destinationPath',
    });
    vi.mocked(updateSettings).mockRejectedValueOnce(refusal);

    let outcome;
    await act(async () => {
      outcome = await result.current.update({ destinationPath: '/books/source/sorted' });
    });

    expect(outcome).toBe(refusal);
    expect(result.current.settings.destinationPath).toBe('/books/sorted');
    expect(result.current.fieldErrors).toEqual({ destinationPath: refusal.message });
    expect(result.current.error).toBeNull();

    // Picking a good folder answers the error.
    vi.mocked(updateSettings).mockResolvedValueOnce(settingsResponse({ destinationPath: '/media/usb/books' }));
    await act(() => result.current.update({ destinationPath: '/media/usb/books' }));

    expect(result.current.fieldErrors).toEqual({});
    expect(result.current.settings.destinationPath).toBe('/media/usb/books');
  });

  it('clears a path error that turning the schedule on caused once the schedule is turned off', async () => {
    const { result } = await renderLoaded();
    vi.mocked(updateSettings)
      .mockRejectedValueOnce(
        new ApiError('The destination folder does not exist. Is the drive connected?', {
          status: 400,
          field: 'destinationPath',
        })
      )
      .mockResolvedValueOnce(settingsResponse());

    await act(() => result.current.update({ scheduleIntervalMinutes: 1440 }));
    expect(result.current.fieldErrors.destinationPath).toMatch(/does not exist/);

    await act(() => result.current.update({ scheduleIntervalMinutes: null }));
    expect(result.current.fieldErrors).toEqual({});
  });

  it('keeps an error about no one field apart', async () => {
    const { result } = await renderLoaded();
    vi.mocked(updateSettings).mockRejectedValueOnce(new ApiError('Could not reach the backend.', { code: 'unreachable' }));

    await act(() => result.current.update({ copySpeed: 'gentle' }));

    expect(result.current.error).toBe('Could not reach the backend.');
    expect(result.current.fieldErrors).toEqual({});
  });

  it('drops a lost-contact error once a refresh gets an answer, but not a refused save', async () => {
    const { result } = await renderLoaded();
    vi.mocked(updateSettings).mockRejectedValueOnce(new ApiError('Could not reach the backend.', { code: 'unreachable' }));
    await act(() => result.current.update({ copySpeed: 'gentle' }));

    await act(() => result.current.refresh());
    expect(result.current.error).toBeNull();

    vi.mocked(updateSettings).mockRejectedValueOnce(new ApiError('Could not save the settings.'));
    await act(() => result.current.update({ copySpeed: 'gentle' }));

    await act(() => result.current.refresh());
    expect(result.current.error).toBe('Could not save the settings.');
  });
});
