import { render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, getRunStatus, getSettings } from '../../api';
import App from '../../App';
import { idleStatus } from '../../test/fixtures';

vi.mock('../../api', async (original) => (await import('../../test/mockApi')).mockApi(original));

const TIMEOUT_MESSAGE =
  'The organizer did not answer in time. A network drive or disk that has stopped responding can cause this: check that it is connected.';

describe('Starting the organizer on the desktop', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getRunStatus).mockResolvedValue(idleStatus());
    window.electronAPI = {
      isMaximized: vi.fn().mockResolvedValue(false),
      onMaximizedChanged: vi.fn(() => () => {}),
      setBackgroundOptions: vi.fn(),
      setTheme: vi.fn(),
      backendStopped: vi.fn().mockResolvedValue(null),
      onBackendStopped: vi.fn(() => () => {}),
    };
  });
  afterEach(() => {
    delete window.electronAPI;
  });

  it('says a read that hangs on a network drive is the drive, not that it is still trying', async () => {
    vi.mocked(getSettings).mockRejectedValue(new ApiError(TIMEOUT_MESSAGE, { code: 'timeout' }));
    render(<App />);

    expect(await screen.findByText(TIMEOUT_MESSAGE)).toBeTruthy();
    expect(screen.queryByText(/Still trying/)).toBeNull();
  });

  it('says why once the app has stopped trying to start the organizer', async () => {
    const reason = 'Another copy of the Book Organizer is still running. Wait a moment, then open the app again.';
    window.electronAPI.backendStopped.mockResolvedValue(reason);
    vi.mocked(getSettings).mockRejectedValue(new ApiError('unreachable', { code: 'unreachable' }));
    render(<App />);

    expect(await screen.findByText(reason)).toBeTruthy();
    expect(screen.getByText("The organizer didn't start")).toBeTruthy();
    expect(screen.queryByText(/Still trying/)).toBeNull();
  });
});
