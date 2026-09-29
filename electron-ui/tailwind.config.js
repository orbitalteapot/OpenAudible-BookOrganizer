/** @type {import('tailwindcss').Config} */

// Every colour is a token defined in src/index.css. Declaring them as channel triplets keeps
// Tailwind's opacity modifiers working (bg-surface/60, ring-accent/70).
const token = (name) => `rgb(var(--color-${name}) / <alpha-value>)`;

// Layout spacing sticks to whole 4px steps (p-2, gap-3, px-5): 8px between related things, 16px
// between groups, 20px inside cards and the page. Half steps are only for sizes and 2px insets (the
// track round a segmented control), never for the space between two things.
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
        'critical-solid': token('critical-solid'),
        'critical-solid-fg': token('critical-solid-fg'),
      },
      // Installed fonts only: the desktop app must look right offline, and the web build's CSP
      // allows no font host. Inter where the user has it, otherwise the platform's own UI face.
      fontFamily: {
        sans: [
          'Inter',
          'InterVariable',
          'system-ui',
          '-apple-system',
          'BlinkMacSystemFont',
          '"Segoe UI Variable Text"',
          '"Segoe UI"',
          'Roboto',
          '"Helvetica Neue"',
          'Arial',
          'sans-serif',
        ],
      },
      // Themed in src/index.css: a soft lift in light mode, nothing in dark.
      boxShadow: {
        card: 'var(--shadow-card)',
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
