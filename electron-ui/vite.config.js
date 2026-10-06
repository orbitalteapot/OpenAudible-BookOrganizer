const { defineConfig } = require('vite');
const react = require('@vitejs/plugin-react');

module.exports = defineConfig({
  plugins: [react()],
  base: './',
  server: {
    port: 5173,
  },
  build: {
    outDir: 'dist',
  },
  test: {
    // Hook and page tests render React; the pure-function tests do not mind the DOM being there.
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.js'],
  },
});
