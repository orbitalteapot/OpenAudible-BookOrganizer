import { render } from '@testing-library/react';
import { vi } from 'vitest';
import * as api from '../api';
import App from '../App';
import { idleStatus, scheduleResponse, settingsResponse } from './fixtures';

/**
 * The whole app against a stubbed backend. The test file mocks the api module with `mockApi` (see
 * mockApi.js); this sets what every endpoint answers unless the test says otherwise.
 */
export function renderApp({
  settings = settingsResponse(),
  status = idleStatus(),
  schedule = scheduleResponse(),
  library = { books: [], skippedRows: 0, warnings: [] },
} = {}) {
  vi.mocked(api.getSettings).mockResolvedValue(settings);
  vi.mocked(api.getRunStatus).mockResolvedValue(status);
  vi.mocked(api.getSchedule).mockResolvedValue(schedule);
  vi.mocked(api.parseLibrary).mockResolvedValue(library);
  return render(<App />);
}
