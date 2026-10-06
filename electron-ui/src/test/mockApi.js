import { vi } from 'vitest';

/**
 * The api module with every request replaced by a mock and the real ApiError kept. For
 * `vi.mock(path, async (original) => (await import('…/test/mockApi')).mockApi(original))`: the
 * factory is hoisted above the file's imports, so it has to load this itself.
 */
export async function mockApi(importOriginal) {
  const actual = await importOriginal();
  return {
    ...actual,
    getSettings: vi.fn(),
    updateSettings: vi.fn(),
    getSchedule: vi.fn(),
    parseLibrary: vi.fn(),
    getBooks: vi.fn(),
    startSort: vi.fn(),
    getRunStatus: vi.fn(),
    cancelSort: vi.fn(),
    healthCheck: vi.fn(),
  };
}
