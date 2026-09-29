import { fireEvent, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, parseLibrary, updateSettings } from '../../api';
import { settingsResponse } from '../../test/fixtures';
import { renderApp } from '../../test/renderApp';

vi.mock('../../api', async (original) => (await import('../../test/mockApi')).mockApi(original));

const book = (title) => ({ title, author: 'Dennis E. Taylor', seriesName: '', narratedBy: '', duration: '10:00:00' });

describe('Library', () => {
  beforeEach(() => vi.clearAllMocks());

  it('reads the export named in the settings on launch', async () => {
    renderApp({ library: { books: [book('We Are Legion'), book('For We Are Many')], skippedRows: 0, warnings: [] } });

    expect(await screen.findByText('2 audiobooks')).toBeTruthy();
    expect(parseLibrary).toHaveBeenCalledTimes(1);
    expect(screen.getByText('/books/library.csv')).toBeTruthy();
  });

  it('explains what to do when no export is set', async () => {
    renderApp({ settings: settingsResponse({ csvPath: null, pathStatus: { csv: 'notSet', source: 'ok', destination: 'ok' } }) });

    expect(await screen.findByText('No library export chosen')).toBeTruthy();
    // The browser build cannot choose files, so it says where the path comes from instead.
    expect(screen.getByText(/Set CSV_PATH in the container/)).toBeTruthy();
    expect(parseLibrary).not.toHaveBeenCalled();
  });

  it('says where to look in the container when the export is not there', async () => {
    renderApp({ settings: settingsResponse({ csvPath: '/data/books.csv', pathStatus: { csv: 'notFound', source: 'ok', destination: 'ok' } }) });
    vi.mocked(parseLibrary).mockRejectedValue(new Error('The library export was not found: /data/books.csv'));

    expect(await screen.findByText("Couldn't read the library export")).toBeTruthy();
    expect(
      screen.getByText(
        'File not found inside the container — check that the folder holding it is mapped to /data and that CSV_PATH names the file'
      )
    ).toBeTruthy();
  });

  it('shows rows the export could not read, folded away', async () => {
    renderApp({ library: { books: [book('We Are Legion')], skippedRows: 1, warnings: ['Row 7: no title'] } });

    const toggle = await screen.findByRole('button', { name: /1 row of the export could not be read/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('Row 7: no title')).toBeTruthy();
  });

  describe('on the desktop', () => {
    beforeEach(() => {
      window.electronAPI = {
        openFile: vi.fn(),
        openFolder: vi.fn(),
        isMaximized: vi.fn().mockResolvedValue(false),
        onMaximizedChanged: vi.fn(() => () => {}),
        setBackgroundOptions: vi.fn(),
        setTheme: vi.fn(),
      };
    });
    afterEach(() => {
      delete window.electronAPI;
    });

    it('says why a chosen export was refused when the reason is not the export', async () => {
      renderApp({ library: { books: [book('We Are Legion')], skippedRows: 0, warnings: [] } });
      await screen.findByText('1 audiobooks');
      vi.mocked(window.electronAPI.openFile).mockResolvedValue('/media/usb/new.csv');
      vi.mocked(updateSettings).mockRejectedValueOnce(
        new ApiError('The destination folder does not exist. Is the drive connected?', {
          status: 400,
          code: 'destinationMissing',
          field: 'destinationPath',
        })
      );

      fireEvent.click(screen.getByRole('button', { name: 'Choose export…' }));

      expect(
        await screen.findByText("Couldn't use this export: The destination folder does not exist. Is the drive connected?")
      ).toBeTruthy();
      // The dialog said what it was for and opened at the export in use.
      expect(window.electronAPI.openFile).toHaveBeenCalledWith(
        expect.objectContaining({ title: 'Choose the CSV export', defaultPath: '/books/library.csv' })
      );
    });

    it('puts focus on the search field when books chosen from the empty page arrive', async () => {
      renderApp({ settings: settingsResponse({ csvPath: null, pathStatus: { csv: 'notSet', source: 'ok', destination: 'ok' } }) });
      const choose = await screen.findByRole('button', { name: 'Choose export…' });
      vi.mocked(window.electronAPI.openFile).mockResolvedValue('/books/library.csv');
      vi.mocked(updateSettings).mockResolvedValueOnce(settingsResponse());
      vi.mocked(parseLibrary).mockResolvedValue({ books: [book('We Are Legion')], skippedRows: 0, warnings: [] });

      choose.focus();
      fireEvent.click(choose);

      await screen.findByText('1 audiobooks');
      await waitFor(() => expect(document.activeElement).toBe(screen.getByRole('searchbox', { name: 'Search books' })));
    });
  });
});
