import { useCallback, useEffect, useState } from 'react';
import TitleBar from './components/TitleBar';
import Sidebar from './components/Sidebar';
import Library from './components/Library';
import SortPanel from './components/SortPanel';
import { getAppConfig } from './api';
import { useIsElectron } from './hooks';

const INITIAL_CONFIG = {
  csvPath: '',
  sourcePath: '',
  destPath: '',
  comparisonMode: 'quick',
};

const INITIAL_RUN = {
  sorting: false,
  progress: null,
  error: null,
};

export default function App() {
  const isElectron = useIsElectron();
  const [currentPage, setCurrentPage] = useState('library');
  const [books, setBooks] = useState([]);
  const [configLoaded, setConfigLoaded] = useState(isElectron);

  // Deliberately two pieces of state. `run` changes two and a half times a second while a sort is
  // going; `config` does not. Keeping them apart, with the pages memoised, means that polling
  // re-renders the progress card and nothing else.
  const [config, setConfig] = useState(INITIAL_CONFIG);
  const [run, setRun] = useState(INITIAL_RUN);

  useEffect(() => {
    if (isElectron) return undefined;

    let active = true;

    getAppConfig()
      .then((serverConfig) => {
        if (!active) return;
        setConfig((prev) => ({
          csvPath: serverConfig.csvPath || prev.csvPath,
          sourcePath: serverConfig.sourcePath || prev.sourcePath,
          destPath: serverConfig.destinationPath || prev.destPath,
          comparisonMode: serverConfig.comparisonMode || prev.comparisonMode,
        }));
      })
      .catch(() => {
        // Surfaced when an action is actually attempted; a failed probe on load is just noise.
      })
      .finally(() => {
        if (active) setConfigLoaded(true);
      });

    return () => {
      active = false;
    };
  }, [isElectron]);

  const handlePageChange = useCallback((page) => setCurrentPage(page), []);

  if (!configLoaded) {
    return <div className="h-full bg-canvas" />;
  }

  return (
    <div className="flex h-full flex-col bg-canvas">
      <TitleBar />

      <div className="flex min-h-0 flex-1">
        <Sidebar currentPage={currentPage} onPageChange={handlePageChange} bookCount={books.length} />

        <main className="flex min-w-0 flex-1 flex-col p-5">
          {currentPage === 'library' ? (
            <Library books={books} setBooks={setBooks} csvPath={config.csvPath} />
          ) : (
            <SortPanel config={config} setConfig={setConfig} run={run} setRun={setRun} />
          )}
        </main>
      </div>
    </div>
  );
}
