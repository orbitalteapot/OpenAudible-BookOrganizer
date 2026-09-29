import { useCallback, useEffect, useState } from 'react';
import { Headphones } from 'lucide-react';
import TitleBar from './components/TitleBar';
import Sidebar from './components/Sidebar';
import Library from './components/Library';
import SortPage from './components/SortPage';
import RunAnnouncer from './components/RunAnnouncer';
import AppNotices from './components/AppNotices';
import { Banner, EmptyState } from './components/ui/Surface';
import { useIsElectron, useLibrary, useRunStatus, useSchedule, useSettings, useTheme } from './hooks';

// How often the folder statuses (and with them the schedule) are asked for again.
const STATUS_REFRESH_MS = 30_000;

/**
 * Shown until the backend has answered with the settings, which every page is built from. One
 * message at a time: on the desktop the app itself says, in a dialog, if the organizer never
 * starts, so the page only says it is taking a while; in the browser, only the page can say that
 * the container is not answering.
 */
function Starting({ error, isElectron }) {
  if (error && isElectron) {
    return (
      <EmptyState
        icon={Headphones}
        title="Starting the organizer…"
        description="This is taking longer than usual. Still trying."
      />
    );
  }

  return (
    <EmptyState icon={Headphones} title="Starting the organizer…">
      {error && <Banner tone="critical">{error}</Banner>}
    </EmptyState>
  );
}

export default function App() {
  const isElectron = useIsElectron();
  const [currentPage, setCurrentPage] = useState('library');
  const { theme, setTheme } = useTheme();

  const settingsState = useSettings();
  const { settings, refresh: refreshSettings } = settingsState;
  const run = useRunStatus();
  const runFinishedUtc = run.status?.state === 'finished' ? run.status.finishedUtc : null;
  const scheduleState = useSchedule({
    settings,
    update: settingsState.update,
    fieldErrors: settingsState.fieldErrors,
    runFinishedUtc,
  });
  const library = useLibrary(settings?.csvPath, settings?.pathStatus?.csv === 'ok', refreshSettings);

  // An automatic sort is due as soon as a schedule is turned on (and at launch, when one was
  // missed), and it starts on the backend's own timer. Watch closely for it so it shows at once.
  const scheduleOn = Boolean(settings?.scheduleIntervalMinutes);
  const { expectRun } = run;
  useEffect(() => {
    if (scheduleOn) expectRun();
  }, [scheduleOn, expectRun]);

  // A run can create the destination, and whatever it found out about the folders is worth
  // showing, so the path statuses are asked for again once it ends.
  useEffect(() => {
    if (runFinishedUtc) refreshSettings();
  }, [runFinishedUtc, refreshSettings]);

  // Folders also come and go on their own: a drive is plugged in or pulled out, a container's volume
  // comes back. So the statuses are asked for again every 30 seconds and whenever the window comes
  // back into view. The schedule follows every settings answer (see useSchedule), so the Folders
  // card and the schedule card are always describing the same moment.
  useEffect(() => {
    const refreshIfVisible = () => {
      if (document.visibilityState === 'visible') refreshSettings();
    };
    const timer = setInterval(refreshSettings, STATUS_REFRESH_MS);
    window.addEventListener('focus', refreshSettings);
    document.addEventListener('visibilitychange', refreshIfVisible);
    return () => {
      clearInterval(timer);
      window.removeEventListener('focus', refreshSettings);
      document.removeEventListener('visibilitychange', refreshIfVisible);
    };
  }, [refreshSettings]);

  // Electron owns the tray and the login item, so it is told whenever the backend confirms a change.
  const settingsLoaded = settings !== null;
  const keepRunningInBackground = settings?.keepRunningInBackground;
  const openAtLogin = settings?.openAtLogin;
  useEffect(() => {
    if (settingsLoaded) window.electronAPI?.setBackgroundOptions?.({ keepRunningInBackground, openAtLogin });
  }, [settingsLoaded, keepRunningInBackground, openAtLogin]);

  const handlePageChange = useCallback((page) => setCurrentPage(page), []);

  // The run pill promises progress, which on a narrow window sits far below the top of the Sort
  // page; the Progress card takes focus (and so scrolls into view) once, then clears this.
  const [focusProgress, setFocusProgress] = useState(false);
  const handleShowProgress = useCallback(() => {
    setCurrentPage('sort');
    setFocusProgress(true);
  }, []);
  const handleProgressFocused = useCallback(() => setFocusProgress(false), []);

  return (
    <div className="flex h-full flex-col bg-canvas">
      {isElectron && <TitleBar />}

      <div className="flex min-h-0 flex-1">
        <Sidebar
          currentPage={currentPage}
          onPageChange={handlePageChange}
          onShowProgress={handleShowProgress}
          bookCount={library.books.length}
          runStatus={run.status}
          theme={theme}
          onThemeChange={setTheme}
        />

        <main className="flex min-w-0 flex-1 flex-col p-5">
          {settings && <AppNotices lostContact={run.error} serverWarnings={settings.serverWarnings} />}
          {!settings ? (
            <Starting error={settingsState.error} isElectron={isElectron} />
          ) : currentPage === 'library' ? (
            <Library
              library={library}
              settings={settings}
              update={settingsState.update}
              fieldErrors={settingsState.fieldErrors}
              isElectron={isElectron}
            />
          ) : (
            <SortPage
              settingsState={settingsState}
              run={run}
              scheduleState={scheduleState}
              isElectron={isElectron}
              focusProgress={focusProgress}
              onProgressFocused={handleProgressFocused}
            />
          )}
        </main>
      </div>

      <RunAnnouncer status={run.status} />
    </div>
  );
}
