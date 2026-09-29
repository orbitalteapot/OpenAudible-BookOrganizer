import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import TitleBar from '../TitleBar';

describe('TitleBar', () => {
  afterEach(() => {
    delete window.electronAPI;
  });

  it('offers Quit while closing the window keeps the app running', async () => {
    let announce;
    window.electronAPI = {
      isMaximized: vi.fn().mockResolvedValue(false),
      onMaximizedChanged: vi.fn(() => () => {}),
      staysInBackground: vi.fn().mockResolvedValue(false),
      onStaysInBackgroundChanged: vi.fn((handler) => {
        announce = handler;
        return () => {};
      }),
      quit: vi.fn(),
    };
    render(<TitleBar />);
    await act(() => Promise.resolve());
    expect(screen.queryByRole('button', { name: 'Quit Book Organizer' })).toBeNull();

    // Background mode turned on, or "Keep running in the background" chosen: on a desktop with no
    // tray (stock GNOME) this is the only way left to quit.
    act(() => announce(true));
    fireEvent.click(screen.getByRole('button', { name: 'Quit Book Organizer' }));

    expect(window.electronAPI.quit).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Close (keeps running in the background)' })).toBeTruthy();
  });
});
