import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, cancelSort, getRunStatus, startSort } from '../../api';
import { idleStatus, runningStatus } from '../../test/fixtures';
import { renderApp } from '../../test/renderApp';

vi.mock('../../api', async (original) => (await import('../../test/mockApi')).mockApi(original));

async function openSortPage(options) {
  renderApp(options);
  fireEvent.click(await screen.findByRole('button', { name: 'Sort' }));
  return screen.findByRole('heading', { name: 'Sort', level: 1 });
}

const progressCard = () => screen.getByRole('heading', { name: 'Progress' }).closest('section');

describe('SortPage', () => {
  beforeEach(() => vi.clearAllMocks());

  it('shows an automatic sort it did not start, with Cancel, and holds Start back', async () => {
    await openSortPage({ status: runningStatus({ trigger: 'scheduled' }) });

    const card = progressCard();
    expect(await within(card).findByText(/Automatic sort running · started/)).toBeTruthy();
    expect(within(card).getByText('42 of 100 books')).toBeTruthy();
    expect(within(card).getByText('We Are Legion (We Are Bob) — Dennis E. Taylor')).toBeTruthy();

    const start = screen.getByRole('button', { name: 'Start sorting' });
    expect(start.getAttribute('aria-disabled')).toBe('true');
    expect(screen.getByText('A sort is already running.')).toBeTruthy();

    // The status pill says so on every page, and takes you here.
    expect(screen.getByRole('button', { name: /Automatic sort running · 42%/ })).toBeTruthy();

    vi.mocked(cancelSort).mockResolvedValueOnce({});
    fireEvent.click(within(card).getByRole('button', { name: 'Cancel' }));
    await waitFor(() => expect(cancelSort).toHaveBeenCalledTimes(1));
  });

  it('follows the sort already running when Start is refused with 409', async () => {
    await openSortPage();
    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('A sort is already running.', { status: 409, code: 'alreadyRunning' })
    );
    vi.mocked(getRunStatus).mockResolvedValue(runningStatus({ trigger: 'scheduled' }));

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));

    expect(await within(progressCard()).findByText(/Automatic sort running/)).toBeTruthy();
    expect(screen.queryByText(/Couldn't start the sort/)).toBeNull();
  });

  it('asks before creating a missing destination, then sorts with it created', async () => {
    await openSortPage();
    vi.mocked(startSort)
      .mockRejectedValueOnce(
        new ApiError('The destination folder does not exist. Is the drive connected?', {
          status: 400,
          code: 'destinationMissing',
          field: 'destinationPath',
        })
      )
      .mockResolvedValueOnce(runningStatus());

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));
    const create = await screen.findByRole('button', { name: 'Create folder and sort' });
    expect(screen.getByText("The destination folder doesn't exist. Is the drive connected?")).toBeTruthy();
    expect(document.activeElement).toBe(create);

    fireEvent.click(create);
    await waitFor(() => expect(startSort).toHaveBeenLastCalledWith({ createDestination: true }));
  });

  it('shows a refused start under the path it was about', async () => {
    await openSortPage({ status: idleStatus() });
    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('Cannot write to the destination folder: Access denied', {
        status: 400,
        code: 'notWritable',
        field: 'destinationPath',
      })
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));

    const destination = await screen.findByLabelText('Destination folder');
    await waitFor(() => expect(destination.getAttribute('aria-invalid')).toBe('true'));
    const hint = document.getElementById(destination.getAttribute('aria-describedby'));
    expect(hint.textContent).toBe('Cannot write to the destination folder: Access denied');
  });

  it('says what a finished run did and lists its problems by kind', async () => {
    await openSortPage({
      status: {
        ...runningStatus(),
        state: 'finished',
        finishedUtc: new Date().toISOString(),
        currentTitle: null,
        counts: { new: 1, updated: 0, moved: 2, upToDate: 5, notFound: 1, failed: 0 },
        problems: [{ kind: 'notFound', book: 'Heaven’s River — Dennis E. Taylor', message: 'No file for this book in the source folder' }],
        problemCount: 1,
      },
    });

    const card = progressCard();
    expect(await within(card).findByText('Sort complete: 1 new, 2 moved, 5 up to date, 1 not found.')).toBeTruthy();
    expect(within(card).getByText('2 books from an older layout were moved into their own folders.')).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Problems (1)' }));
    expect(within(card).getByText('No file in the source folder')).toBeTruthy();
    expect(within(card).getByText('Heaven’s River — Dennis E. Taylor')).toBeTruthy();
  });
});
