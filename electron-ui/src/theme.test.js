import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

// The palettes are checked where they are defined, so a colour tweak that breaks contrast fails here
// rather than in front of someone who cannot read the result.
const css = readFileSync(join(dirname(fileURLToPath(import.meta.url)), 'index.css'), 'utf8');

/** The `--color-*` channel triplets declared in the first block opened by `selector`. */
function tokensIn(selector) {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) throw new Error(`No "${selector}" block in index.css`);
  const body = css.slice(start, css.indexOf('}', start));

  return Object.fromEntries(
    [...body.matchAll(/--color-([\w-]+):\s*(\d+) (\d+) (\d+);/g)].map(([, name, ...rgb]) => [name, rgb.map(Number)])
  );
}

/** WCAG 2.x relative luminance and contrast ratio. */
function luminance(rgb) {
  const [r, g, b] = rgb.map((channel) => {
    const c = channel / 255;
    return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
}

function contrast(a, b) {
  const [light, dark] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (light + 0.05) / (dark + 0.05);
}

const light = tokensIn('\n  :root');
const darkExplicit = tokensIn(":root[data-theme='dark']");
const darkSystem = tokensIn(":root:not([data-theme='light'])");

// Dark only overrides the colours that differ, so it inherits the rest from :root.
const PALETTES = { light, dark: { ...light, ...darkExplicit } };

const GROUNDS = ['canvas', 'surface', 'raised'];
const TEXT = ['fg', 'fg-muted', 'fg-subtle', 'accent', 'accent-hover', 'positive', 'caution', 'critical'];
// Edges that identify a control, and the focus ring (drawn in the accent).
const BOUNDARIES = ['line-strong', 'accent'];
// Text or icons drawn on a filled colour.
const ON_FILL = [
  ['accent-fg', 'accent'],
  ['accent-fg', 'accent-hover'],
  ['critical-solid-fg', 'critical-solid'],
];

describe('theme tokens', () => {
  it('writes the dark palette identically for Dark and for System on a dark system', () => {
    expect(darkSystem).toEqual(darkExplicit);
  });

  it('gives the dark palette a value for every colour the light one themes', () => {
    const shared = ['critical-solid', 'critical-solid-fg'];
    expect(Object.keys(darkExplicit).sort()).toEqual(Object.keys(light).filter((name) => !shared.includes(name)).sort());
  });

  describe.each(Object.entries(PALETTES))('%s', (_, palette) => {
    it.each(TEXT.flatMap((fg) => GROUNDS.map((ground) => [fg, ground])))(
      '%s text on %s clears 4.5:1',
      (fg, ground) => {
        expect(contrast(palette[fg], palette[ground])).toBeGreaterThanOrEqual(4.5);
      }
    );

    it.each(BOUNDARIES.flatMap((edge) => GROUNDS.map((ground) => [edge, ground])))(
      '%s as a boundary on %s clears 3:1',
      (edge, ground) => {
        expect(contrast(palette[edge], palette[ground])).toBeGreaterThanOrEqual(3);
      }
    );

    it.each(ON_FILL)('%s on %s clears 4.5:1', (fg, fill) => {
      expect(contrast(palette[fg], palette[fill])).toBeGreaterThanOrEqual(4.5);
    });
  });
});
