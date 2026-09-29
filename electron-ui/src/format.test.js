import { describe, expect, it } from 'vitest';
import { formatInterval } from './format';

describe('formatInterval', () => {
  it.each([
    [null, 'Off'],
    [60, 'Every hour'],
    [360, 'Every 6 hours'],
    [1440, 'Every day'],
    [10080, 'Every 7 days'],
    [45, 'Every 45 minutes'],
  ])('describes %s minutes as "%s"', (minutes, expected) => {
    expect(formatInterval(minutes)).toBe(expected);
  });
});
