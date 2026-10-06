import { afterEach } from 'vitest';
import { cleanup } from '@testing-library/react';

// Testing Library only unmounts between tests on its own when the runner exposes globals.
afterEach(cleanup);

// jsdom does no layout, so it has no ResizeObserver. Nothing observed ever resizes there anyway.
if (!globalThis.ResizeObserver) {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}
