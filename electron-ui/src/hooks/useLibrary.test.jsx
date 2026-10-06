import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, parseLibrary } from '../api';
import useLibrary from './useLibrary';

vi.mock('../api', async (importOriginal) => ({
  ...(await importOriginal()),
  parseLibrary: vi.fn(),
}));

const book = { title: 'Dune' };

describe('useLibrary', () => {
  beforeEach(() => {
    vi.mocked(parseLibrary).mockReset();
  });

  it('drops the previous export when a different one cannot be read', async () => {
    vi.mocked(parseLibrary).mockResolvedValueOnce({ books: [book] });
    const { result, rerender } = renderHook(({ path }) => useLibrary(path), {
      initialProps: { path: '/a.csv' },
    });
    await waitFor(() => expect(result.current.books).toHaveLength(1));

    vi.mocked(parseLibrary).mockRejectedValueOnce(new ApiError('Not a library export', { status: 400, code: 'csvInvalid' }));
    rerender({ path: '/b.csv' });
    await waitFor(() => expect(result.current.error).toBe('Not a library export'));
    expect(result.current.books).toHaveLength(0);
    expect(result.current.loaded).toBe(false);
  });

  it('keeps the list on screen when re-reading the same export fails', async () => {
    vi.mocked(parseLibrary).mockResolvedValueOnce({ books: [book] });
    const { result } = renderHook(() => useLibrary('/a.csv'));
    await waitFor(() => expect(result.current.books).toHaveLength(1));

    vi.mocked(parseLibrary).mockRejectedValueOnce(new ApiError('Busy', { status: 409 }));
    await act(() => result.current.reload());
    await waitFor(() => expect(result.current.error).toBe('Busy'));
    expect(result.current.books).toHaveLength(1);
    expect(result.current.loaded).toBe(true);
  });

  it('reads the export again once it turns up after a failed read', async () => {
    vi.mocked(parseLibrary).mockRejectedValueOnce(new ApiError('The library export was not found', { status: 404 }));
    const { result, rerender } = renderHook(({ found }) => useLibrary('/a.csv', found), {
      initialProps: { found: false },
    });
    await waitFor(() => expect(result.current.error).toBe('The library export was not found'));

    vi.mocked(parseLibrary).mockResolvedValueOnce({ books: [book] });
    rerender({ found: true });
    await waitFor(() => expect(result.current.books).toHaveLength(1));
    expect(result.current.error).toBeNull();
    expect(parseLibrary).toHaveBeenCalledTimes(2);
  });

  it('does not read a found export twice on launch', async () => {
    vi.mocked(parseLibrary).mockResolvedValue({ books: [book] });
    const { result, rerender } = renderHook(({ path, found }) => useLibrary(path, found), {
      initialProps: { path: undefined, found: false },
    });
    rerender({ path: '/a.csv', found: true });
    await waitFor(() => expect(result.current.books).toHaveLength(1));
    expect(parseLibrary).toHaveBeenCalledTimes(1);
  });

  it('asks for the path status again when it read an export the status says is missing', async () => {
    vi.mocked(parseLibrary).mockResolvedValue({ books: [book] });
    const refreshStatus = vi.fn();
    const { result, rerender } = renderHook(({ found }) => useLibrary('/a.csv', found, refreshStatus), {
      initialProps: { found: false },
    });
    await waitFor(() => expect(result.current.books).toHaveLength(1));
    expect(refreshStatus).toHaveBeenCalledTimes(1);

    // Once the status agrees, a reload has nothing to correct.
    rerender({ found: true });
    await act(() => result.current.reload());
    expect(refreshStatus).toHaveBeenCalledTimes(1);
  });
});
