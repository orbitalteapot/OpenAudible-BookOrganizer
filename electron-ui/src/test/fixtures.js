// Backend responses as the API sends them, for tests to adjust.

export const settingsResponse = (overrides = {}) => ({
  csvPath: '/books/library.csv',
  sourcePath: '/books/source',
  destinationPath: '/books/sorted',
  comparisonMode: 'quick',
  copySpeed: 'normal',
  scheduleIntervalMinutes: null,
  keepRunningInBackground: false,
  openAtLogin: false,
  locks: { paths: false, schedule: false },
  pathStatus: { csv: 'ok', source: 'ok', destination: 'ok' },
  pathMessages: { csv: null, source: null, destination: null },
  serverWarnings: [],
  ...overrides,
});

export const counts = (overrides = {}) => ({
  new: 0,
  updated: 0,
  moved: 0,
  upToDate: 0,
  notFound: 0,
  failed: 0,
  ...overrides,
});

export const idleStatus = () => ({
  state: 'idle',
  trigger: null,
  startedUtc: null,
  finishedUtc: null,
  totalBooks: 0,
  currentBook: 0,
  percentage: 0,
  currentTitle: null,
  counts: counts(),
  problems: [],
  problemCount: 0,
  isCanceled: false,
  preparing: false,
  error: null,
  errorCode: null,
  errorField: null,
});

export const runningStatus = (overrides = {}) => ({
  ...idleStatus(),
  state: 'running',
  trigger: 'manual',
  startedUtc: new Date().toISOString(),
  totalBooks: 100,
  currentBook: 42,
  percentage: 42,
  currentTitle: 'We Are Legion (We Are Bob) — Dennis E. Taylor',
  counts: counts({ new: 2, upToDate: 40 }),
  ...overrides,
});

export const scheduleResponse = (overrides = {}) => ({
  intervalMinutes: null,
  locked: false,
  nextRunUtc: null,
  lastRun: null,
  retrying: false,
  blockedReason: null,
  ...overrides,
});
