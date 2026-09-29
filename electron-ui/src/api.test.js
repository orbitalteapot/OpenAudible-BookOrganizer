import { afterEach, describe, expect, it, vi } from 'vitest';
import { getSettings, updateSettings } from './api';

function answer(status, text) {
  return vi.fn().mockResolvedValue({ ok: status < 400, status, text: () => Promise.resolve(text) });
}

describe('request errors', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('treats a proxy answering for a restarting container as the organizer not answering', async () => {
    vi.stubGlobal('fetch', answer(502, '<html><body>502 Bad Gateway</body></html>'));

    await expect(getSettings()).rejects.toMatchObject({ code: 'unreachable', status: 502 });
    await expect(getSettings()).rejects.not.toThrow(/HTTP/);
  });

  it('treats an answer that is not JSON as the organizer not answering', async () => {
    vi.stubGlobal('fetch', answer(404, '404 page not found'));

    await expect(getSettings()).rejects.toMatchObject({ code: 'unreachable' });
  });

  it("keeps the backend's own refusal, with its code and field", async () => {
    vi.stubGlobal(
      'fetch',
      answer(400, JSON.stringify({ error: 'No source folder is set.', code: 'notSet', field: 'sourcePath' }))
    );

    await expect(updateSettings({ sourcePath: '' })).rejects.toMatchObject({
      message: 'No source folder is set.',
      code: 'notSet',
      field: 'sourcePath',
    });
  });
});
