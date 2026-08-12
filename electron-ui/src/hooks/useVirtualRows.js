import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

/**
 * Which slice of the list to render, and how tall the spacers standing in for the rest must be.
 *
 * Pure, and exported, so the arithmetic can be tested directly: every defect this has had was in
 * these five lines rather than in the wiring around them.
 */
export function computeWindow({ count, rowHeight, overscan, viewportHeight, scrollTop }) {
  const visibleCount = Math.ceil(viewportHeight / rowHeight) + overscan * 2;

  // Clamped to the last full window rather than taken from scrollTop alone. Reloading a smaller
  // library shrinks the list without moving the scroll position, and for the frame before the
  // browser clamps the scroll and tells us about it, an unclamped start points past the end: no
  // rows at all, and a spacer taller than the content it is standing in for.
  const maxStart = Math.max(0, count - visibleCount);
  const start = Math.min(Math.max(0, Math.floor(scrollTop / rowHeight) - overscan), maxStart);
  const end = Math.min(count, start + visibleCount);

  return {
    start,
    end,
    paddingTop: start * rowHeight,
    paddingBottom: Math.max(0, (count - end) * rowHeight),
  };
}

/**
 * Renders only the rows that are actually on screen.
 *
 * A 10,000 book library is perfectly normal, and rendering 10,000 rows costs tens of thousands of
 * DOM nodes: the first paint stalls for seconds and every scroll frame drops. Windowing keeps the
 * node count proportional to the viewport instead of the collection, so scrolling stays smooth
 * whether the library holds fifty books or fifty thousand.
 *
 * Written by hand rather than pulled from a package: it is forty lines, and the page is served
 * under a strict CSP where every added dependency is one more thing to vet.
 *
 * @param count      total number of rows
 * @param rowHeight  fixed pixel height of one row
 * @param overscan   extra rows rendered above and below the viewport, to cover fast scrolls
 */
export default function useVirtualRows({ count, rowHeight, overscan = 6 }) {
  const scrollRef = useRef(null);
  const [viewportHeight, setViewportHeight] = useState(0);
  const [scrollTop, setScrollTop] = useState(0);

  useLayoutEffect(() => {
    const element = scrollRef.current;
    if (!element) return undefined;

    setViewportHeight(element.clientHeight);

    const observer = new ResizeObserver(([entry]) => {
      setViewportHeight(entry.contentRect.height);
    });
    observer.observe(element);

    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    const element = scrollRef.current;
    if (!element) return undefined;

    // Coalesce to one update per frame: a trackpad fling fires scroll far more often than the
    // screen refreshes, and re-rendering on every event is what makes windowed lists feel worse
    // than the naive version they replaced.
    let frame = 0;
    const onScroll = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        setScrollTop(element.scrollTop);
      });
    };

    element.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      element.removeEventListener('scroll', onScroll);
      if (frame) cancelAnimationFrame(frame);
    };
  }, []);

  const scrollToTop = useCallback(() => {
    const element = scrollRef.current;
    if (element) element.scrollTop = 0;
    setScrollTop(0);
  }, []);

  return {
    scrollRef,
    scrollToTop,
    ...computeWindow({ count, rowHeight, overscan, viewportHeight, scrollTop }),
  };
}
