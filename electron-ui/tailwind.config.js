/** @type {import('tailwindcss').Config} */

// Every colour is a token defined in src/index.css. Declaring them as channel triplets keeps
// Tailwind's opacity modifiers working (bg-surface/60, ring-accent/70).
const token = (name) => `rgb(var(--color-${name}) / <alpha-value>)`;

module.exports = {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        canvas: token('canvas'),
        surface: token('surface'),
        raised: token('raised'),
        line: token('line'),
        'line-strong': token('line-strong'),

        fg: token('fg'),
        'fg-muted': token('fg-muted'),
        'fg-subtle': token('fg-subtle'),

        accent: token('accent'),
        'accent-hover': token('accent-hover'),
        'accent-fg': token('accent-fg'),

        positive: token('positive'),
        caution: token('caution'),
        critical: token('critical'),
      },
      borderRadius: {
        sm: 'var(--radius-sm)',
        DEFAULT: 'var(--radius)',
        lg: 'var(--radius-lg)',
      },
      height: {
        control: 'var(--control-height)',
      },
      minHeight: {
        control: 'var(--control-height)',
      },
      fontSize: {
        // A deliberately short scale: meta, body, emphasis, section, page.
        '2xs': ['11px', '16px'],
        xs: ['12px', '18px'],
        sm: ['13px', '20px'],
        base: ['14px', '21px'],
        lg: ['16px', '24px'],
        xl: ['20px', '28px'],
      },
    },
  },
  plugins: [],
};
