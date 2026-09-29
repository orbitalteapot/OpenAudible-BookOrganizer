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
});
