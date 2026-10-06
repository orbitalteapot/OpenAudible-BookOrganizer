// @vitest-environment node
import { EventEmitter } from 'node:events';
import { createRequire } from 'node:module';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const require = createRequire(import.meta.url);
const childProcess = require('child_process');

/** A backend process that runs until the test ends it. */
function fakeProcess() {
  const proc = new EventEmitter();
  proc.pid = 1234;
  proc.stdout = new EventEmitter();
  proc.stderr = new EventEmitter();
  proc.kill = vi.fn();
  return proc;
}

describe('the backend exiting on its own', () => {
  const realSpawn = childProcess.spawn;
  const backendUrl = process.env.OABO_BACKEND_URL;
  let spawned;
  let backend;
  let hooks;

  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'] });
    delete process.env.OABO_BACKEND_URL;
    spawned = [];
    // backend.js takes spawn from the module as it loads, so it is replaced first.
    childProcess.spawn = vi.fn((_file, _args, options) => {
      const proc = fakeProcess();
      spawned.push({ proc, url: options.env.ASPNETCORE_URLS });
      return proc;
    });
    delete require.cache[require.resolve('./backend')];
    backend = require('./backend');
    hooks = { onRestarted: vi.fn(), onStoppedForGood: vi.fn() };
    await backend.start({ packaged: false, settingsPath: '/tmp/settings.json', ...hooks });
  });

  afterEach(() => {
    backend.stop();
    childProcess.spawn = realSpawn;
    if (backendUrl !== undefined) process.env.OABO_BACKEND_URL = backendUrl;
    vi.useRealTimers();
  });

  const exitLast = (code = 1) => spawned.at(-1).proc.emit('exit', code, null);

  it('is started again on a new port, and the app is told where', async () => {
    exitLast();
    await vi.advanceTimersByTimeAsync(1_000);
    await vi.waitFor(() => expect(hooks.onRestarted).toHaveBeenCalledTimes(1));

    expect(spawned).toHaveLength(2);
    expect(hooks.onRestarted).toHaveBeenCalledWith(spawned[1].url);
    expect(backend.url()).toBe(spawned[1].url);
    expect(backend.stoppedForGood()).toBe(false);
    expect(hooks.onStoppedForGood).not.toHaveBeenCalled();
  });

  it('is left stopped, with the reason, once it keeps exiting', async () => {
    for (const wait of [1_000, 5_000, 15_000]) {
      const count = spawned.length;
      exitLast();
      await vi.advanceTimersByTimeAsync(wait);
      await vi.waitFor(() => expect(spawned).toHaveLength(count + 1));
    }

    exitLast(75);
    await vi.advanceTimersByTimeAsync(60_000);

    expect(spawned).toHaveLength(4);
    expect(backend.stoppedForGood()).toBe(true);
    expect(hooks.onStoppedForGood).toHaveBeenCalledWith(expect.stringMatching(/Another copy/));
  });

  it('gets every restart again after running for a while', async () => {
    for (let restart = 0; restart < 5; restart += 1) {
      await vi.advanceTimersByTimeAsync(60_000);
      const count = spawned.length;
      exitLast();
      await vi.advanceTimersByTimeAsync(1_000);
      await vi.waitFor(() => expect(spawned).toHaveLength(count + 1));
    }

    expect(hooks.onStoppedForGood).not.toHaveBeenCalled();
  });

  it('is not started again once the app is quitting', async () => {
    backend.stop();
    exitLast();
    await vi.advanceTimersByTimeAsync(60_000);

    expect(spawned).toHaveLength(1);
    expect(hooks.onStoppedForGood).not.toHaveBeenCalled();
  });
});
