import { fireEvent, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, cancelSort, getRunStatus, getSettings, startSort, updateSettings } from '../../api';
import { idleStatus, runningStatus, settingsResponse } from '../../test/fixtures';
import { renderApp } from '../../test/renderApp';

vi.mock('../../api', async (original) => (await import('../../test/mockApi')).mockApi(original));

async function openSortPage(options) {
  renderApp(options);
  fireEvent.click(await screen.findByRole('button', { name: 'Sort' }));
  return screen.findByRole('heading', { name: 'Sort', level: 1 });
}

/** The status line under a path's field: the last of the descriptions it points at. */
const statusLine = (input) => document.getElementById(input.getAttribute('aria-describedby').split(' ').pop());

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
    // The drive was unplugged after the page last heard about the folders.
    vi.mocked(getSettings).mockResolvedValue(
      settingsResponse({ pathStatus: { csv: 'ok', source: 'ok', destination: 'notFound' } })
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));
    const create = await screen.findByRole('button', { name: 'Create folder and sort' });
    expect(screen.getByText("The destination folder doesn't exist. Is the drive connected?")).toBeTruthy();
    expect(document.activeElement).toBe(create);

    // The row agrees with the question instead of still saying "Found".
    const destination = screen.getByLabelText('Destination folder');
    const hint = statusLine(destination);
    await waitFor(() => expect(hint.textContent).toMatch(/^Folder not found/));

    fireEvent.click(create);
    await waitFor(() => expect(startSort).toHaveBeenLastCalledWith({ createDestination: true }));
  });

  it('goes back to Start sorting when the create-folder question is dismissed', async () => {
    await openSortPage();
    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('The destination folder does not exist. Is the drive connected?', {
        status: 400,
        code: 'destinationMissing',
        field: 'destinationPath',
      })
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));
    await screen.findByRole('button', { name: 'Create folder and sort' });
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('button', { name: 'Start sorting' })));
  });

  it('says what each folder is for, and that the source is only read', async () => {
    await openSortPage();

    expect(screen.getByText('Made in OpenAudible with File > Export.')).toBeTruthy();
    expect(screen.getByText(/Files here are only read, never changed\./)).toBeTruthy();
    expect(screen.getByText(/The source folder is never changed\./)).toBeTruthy();
  });

  it('asks for the folder statuses again when the window comes back into view', async () => {
    await openSortPage();
    const destination = screen.getByLabelText('Destination folder');
    expect(statusLine(destination).textContent).toBe('Found');

    // The drive was pulled out while the window was in the background.
    vi.mocked(getSettings).mockResolvedValue(
      settingsResponse({ pathStatus: { csv: 'ok', source: 'ok', destination: 'notFound' } })
    );
    fireEvent.focus(window);

    await waitFor(() => expect(statusLine(destination).textContent).toMatch(/^Folder not found/));
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
    const hint = statusLine(destination);
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
    expect(within(card).getByText('2 books that an older version left in the destination folder were moved into their own folders there.')).toBeTruthy();

    fireEvent.click(within(card).getByRole('button', { name: 'Problems (1)' }));
    expect(within(card).getByText('No file in the source folder')).toBeTruthy();
    expect(within(card).getByText('Heaven’s River — Dennis E. Taylor')).toBeTruthy();
  });

  it('holds the arrows on an option until the backend has answered the last choice', async () => {
    await openSortPage();
    let reply;
    vi.mocked(updateSettings)
      .mockReturnValueOnce(new Promise((resolve) => (reply = resolve)))
      .mockResolvedValue(settingsResponse({ comparisonMode: 'full' }));

    const group = screen.getByRole('radiogroup', { name: 'Update check' });
    const quick = within(group).getByRole('radio', { name: 'Quick' });
    quick.focus();
    fireEvent.keyDown(quick, { key: 'ArrowRight' });
    // Back again before the reply: counting from the old value, this would choose Verify again.
    fireEvent.keyDown(document.activeElement, { key: 'ArrowLeft' });

    reply(settingsResponse({ comparisonMode: 'full' }));
    const verify = within(group).getByRole('radio', { name: 'Verify contents' });
    await waitFor(() => expect(verify.getAttribute('aria-checked')).toBe('true'));
    expect(document.activeElement).toBe(verify);
    expect(updateSettings).toHaveBeenCalledTimes(1);
  });

  it('drops a refused start once the folder it was about turns up', async () => {
    const unplugged = settingsResponse({ pathStatus: { csv: 'ok', source: 'notFound', destination: 'ok' } });
    await openSortPage({ settings: unplugged });
    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('The source folder was not found: /books/source', {
        status: 400,
        code: 'notFound',
        field: 'sourcePath',
      })
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));
    const source = screen.getByLabelText('Source folder');
    await waitFor(() => expect(statusLine(source).textContent).toBe('The source folder was not found: /books/source'));
    expect(screen.getByText(/Couldn't start the sort/)).toBeTruthy();

    // The drive is plugged back in and the window comes back into view.
    vi.mocked(getSettings).mockResolvedValue(settingsResponse());
    fireEvent.focus(window);

    await waitFor(() => expect(statusLine(source).textContent).toBe('Found'));
    expect(screen.queryByText(/Couldn't start the sort/)).toBeNull();
  });

  it('never offers to create a destination the server sets', async () => {
    await openSortPage({ settings: settingsResponse({ locks: { paths: true, schedule: false } }) });
    vi.mocked(startSort).mockRejectedValueOnce(
      new ApiError('The destination folder /destination was not found inside the container. Check the volume mapping for DESTINATION_PATH.', {
        status: 400,
        code: 'destinationMissing',
        field: 'destinationPath',
      })
    );

    fireEvent.click(screen.getByRole('button', { name: 'Start sorting' }));

    expect(await screen.findByText(/Couldn't start the sort: The destination folder \/destination was not found inside the container/)).toBeTruthy();
    expect(screen.queryByRole('button', { name: 'Create folder and sort' })).toBeNull();
  });

  it('keeps Start sorting out of the scrolling cards, so it is always on screen', async () => {
    await openSortPage();

    const start = screen.getByRole('button', { name: 'Start sorting' });
    const cards = screen.getByRole('heading', { name: 'Folders' }).closest('section').parentElement.parentElement;
    expect(cards.contains(start)).toBe(false);
  });

  it('takes the run pill to the Progress card, from any page', async () => {
    renderApp({ status: runningStatus() });

    fireEvent.click(await screen.findByRole('button', { name: /Sorting… 42%\. Show progress/ }));

    await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('heading', { name: 'Progress' })));
  });
});
