import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';

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

  const visibleCount = Math.ceil(viewportHeight / rowHeight) + overscan * 2;
  const start = Math.max(0, Math.floor(scrollTop / rowHeight) - overscan);
  const end = Math.min(count, start + visibleCount);

  return {
    scrollRef,
    start,
    end,
    scrollToTop,
    paddingTop: start * rowHeight,
    paddingBottom: Math.max(0, (count - end) * rowHeight),
  };
}
