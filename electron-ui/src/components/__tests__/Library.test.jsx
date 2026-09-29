import { fireEvent, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { parseLibrary } from '../../api';
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

  it('shows rows the export could not read, folded away', async () => {
    renderApp({ library: { books: [book('We Are Legion')], skippedRows: 1, warnings: ['Row 7: no title'] } });

    const toggle = await screen.findByRole('button', { name: /1 row of the export could not be read/ });
    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    fireEvent.click(toggle);
    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(screen.getByText('Row 7: no title')).toBeTruthy();
  });
});
