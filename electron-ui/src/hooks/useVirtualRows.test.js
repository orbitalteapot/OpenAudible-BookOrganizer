import { describe, expect, it } from 'vitest';
import { computeWindow } from './useVirtualRows';

// A 500px viewport of 40px rows, the shape the library table actually runs at.
const view = (overrides = {}) => ({
  count: 1000,
  rowHeight: 40,
  overscan: 6,
  viewportHeight: 500,
  scrollTop: 0,
  ...overrides,
});

/** The invariant the whole thing exists to hold: the spacers plus the rows equal the real list. */
const totalHeight = (window, { count, rowHeight }) =>
  window.paddingTop + (window.end - window.start) * rowHeight + window.paddingBottom === count * rowHeight;

describe('computeWindow', () => {
  it('renders a viewport of rows plus overscan, not the whole library', () => {
    const options = view();
    const window = computeWindow(options);

    expect(window.start).toBe(0);
    expect(window.end).toBe(Math.ceil(500 / 40) + 12);
    expect(window.end).toBeLessThan(options.count);
  });

  it('accounts for every row it does not render', () => {
    for (const scrollTop of [0, 400, 4000, 20000, 39600]) {
      const options = view({ scrollTop });
      expect(totalHeight(computeWindow(options), options)).toBe(true);
    }
  });

  it('moves the window as the list is scrolled', () => {
    const top = computeWindow(view({ scrollTop: 0 }));
    const middle = computeWindow(view({ scrollTop: 8000 }));

    expect(middle.start).toBeGreaterThan(top.start);
    expect(middle.start).toBe(200 - 6);
    expect(middle.paddingTop).toBe(middle.start * 40);
  });

  it('keeps overscan above the viewport once there is room for it', () => {
    // Scrolled to row 100, the window starts six rows earlier so a fast scroll upwards has
    // something to show before the next frame.
    expect(computeWindow(view({ scrollTop: 100 * 40 })).start).toBe(94);
  });

  it('does not overscan past the start of the list', () => {
    expect(computeWindow(view({ scrollTop: 40 })).start).toBe(0);
    expect(computeWindow(view({ scrollTop: 0 })).paddingTop).toBe(0);
  });

  it('stops at the end of the list', () => {
    const options = view({ scrollTop: 1000 * 40 });
    const window = computeWindow(options);

    expect(window.end).toBe(1000);
    expect(window.paddingBottom).toBe(0);
    expect(totalHeight(window, options)).toBe(true);
  });

  it('never starts past the end when the list shrinks under a stale scroll position', () => {
    // Reloading a smaller library shrinks the list without moving the scroll position. The
    // browser clamps the scroll a frame later; until it does, an unclamped start pointed past the
    // end and the table rendered no rows behind a spacer taller than the content.
    const options = view({ count: 12, scrollTop: 39600 });
    const window = computeWindow(options);

    expect(window.start).toBe(0);
    expect(window.end).toBe(12);
    expect(window.end).toBeGreaterThan(window.start);
    expect(totalHeight(window, options)).toBe(true);
  });

  it('renders every row of a library smaller than the viewport', () => {
    const options = view({ count: 3 });
    const window = computeWindow(options);

    expect(window.start).toBe(0);
    expect(window.end).toBe(3);
    expect(window.paddingTop).toBe(0);
    expect(window.paddingBottom).toBe(0);
  });

  it('holds together for an empty library', () => {
    const window = computeWindow(view({ count: 0 }));

    expect(window.start).toBe(0);
    expect(window.end).toBe(0);
    expect(window.paddingTop).toBe(0);
    expect(window.paddingBottom).toBe(0);
  });

  it('survives the first render, before the viewport has been measured', () => {
    const options = view({ viewportHeight: 0 });
    const window = computeWindow(options);

    expect(window.start).toBe(0);
    expect(window.end).toBe(12); // overscan alone, replaced as soon as the observer reports
    expect(totalHeight(window, options)).toBe(true);
  });

  it('gives each rendered row a distinct position in the list', () => {
    // The rows are keyed by position, so the window must never hand out the same index twice.
    const window = computeWindow(view({ scrollTop: 8000 }));
    const positions = [];
    for (let index = 0; index < window.end - window.start; index += 1) {
      positions.push(window.start + index);
    }

    expect(new Set(positions).size).toBe(positions.length);
    expect(Math.max(...positions)).toBeLessThan(1000);
  });
});
