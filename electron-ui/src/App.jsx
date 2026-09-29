import { useCallback, useEffect, useState } from 'react';
import { Headphones } from 'lucide-react';
import TitleBar from './components/TitleBar';
import Sidebar from './components/Sidebar';
import Library from './components/Library';
import SortPage from './components/SortPage';
import RunAnnouncer from './components/RunAnnouncer';
import { Banner, EmptyState } from './components/ui/Surface';
import { useIsElectron, useLibrary, useRunStatus, useSchedule, useSettings, useTheme } from './hooks';

/** Shown until the backend has answered with the settings, which every page is built from. */
function Starting({ error }) {
  return (
    <EmptyState
      icon={Headphones}
      title="Starting the organizer…"
      description={error ? 'The backend is not answering yet. Still trying.' : undefined}
    >
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
  const scheduleState = useSchedule({ settings, update: settingsState.update, runFinishedUtc });
  const library = useLibrary(settings?.csvPath);

  // A run can create the destination, and whatever it found out about the folders is worth
  // showing, so the path statuses are asked for again once it ends.
  useEffect(() => {
    if (runFinishedUtc) refreshSettings();
  }, [runFinishedUtc, refreshSettings]);

  // Electron owns the tray and the login item, so it is told whenever the backend confirms a change.
  const settingsLoaded = settings !== null;
  const keepRunningInBackground = settings?.keepRunningInBackground;
  const openAtLogin = settings?.openAtLogin;
  useEffect(() => {
    if (settingsLoaded) window.electronAPI?.setBackgroundOptions?.({ keepRunningInBackground, openAtLogin });
  }, [settingsLoaded, keepRunningInBackground, openAtLogin]);

  const handlePageChange = useCallback((page) => setCurrentPage(page), []);

  return (
    <div className="flex h-full flex-col bg-canvas">
      {isElectron && <TitleBar />}

      <div className="flex min-h-0 flex-1">
        <Sidebar
          currentPage={currentPage}
          onPageChange={handlePageChange}
          bookCount={library.books.length}
          runStatus={run.status}
          theme={theme}
          onThemeChange={setTheme}
        />

        <main className="flex min-w-0 flex-1 flex-col p-5">
          {!settings ? (
            <Starting error={settingsState.error} />
          ) : currentPage === 'library' ? (
            <Library
              library={library}
              settings={settings}
              update={settingsState.update}
              fieldErrors={settingsState.fieldErrors}
              isElectron={isElectron}
            />
          ) : (
            <SortPage settingsState={settingsState} run={run} scheduleState={scheduleState} isElectron={isElectron} />
          )}
        </main>
      </div>

      <RunAnnouncer status={run.status} />
    </div>
  );
}
