import { afterEach, describe, expect, it, vi } from 'vitest';
import { getSettings, startSort, updateSettings } from './api';

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

  it('gives up on any request that takes too long, saves and starts included', async () => {
    // As fetch does when its AbortSignal.timeout fires.
    const fetch = vi.fn().mockRejectedValue(new DOMException('The operation timed out.', 'TimeoutError'));
    vi.stubGlobal('fetch', fetch);

    await expect(updateSettings({ copySpeed: 'gentle' })).rejects.toMatchObject({ code: 'timeout' });
    await expect(startSort()).rejects.toMatchObject({ code: 'timeout' });
    for (const [, options] of fetch.mock.calls) expect(options.signal).toBeInstanceOf(AbortSignal);
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
