import { useEffect, useState } from 'react';
import TitleBar from './components/TitleBar';
import Sidebar from './components/Sidebar';
import Library from './components/Library';
import SortPanel from './components/SortPanel';
import { getAppConfig } from './api';

export default function App() {
  const [currentPage, setCurrentPage] = useState('library');
  const [books, setBooks] = useState([]);
  const [configLoaded, setConfigLoaded] = useState(false);

  const isBrowser = typeof window !== 'undefined';
  const isElectron = isBrowser && !!window.electronAPI;

  // Sort state lifted here so it persists across page switches
  const [sortState, setSortState] = useState({
    csvPath: '',
    sourcePath: '',
    destPath: '',
    sorting: false,
    progress: null,
    error: null,
  });

  useEffect(() => {
    if (isElectron) {
      setConfigLoaded(true);
      return;
    }

    let active = true;

    getAppConfig()
      .then((config) => {
        if (!active) return;
        setSortState((prev) => ({
          ...prev,
          csvPath: config.csvPath || prev.csvPath,
          sourcePath: config.sourcePath || prev.sourcePath,
          destPath: config.destinationPath || prev.destPath,
        }));
      })
      .catch(() => {
        // The API panel will surface request failures when actions are invoked.
      })
      .finally(() => {
        if (active) setConfigLoaded(true);
      });

    return () => {
      active = false;
    };
  }, [isElectron]);

  if (!configLoaded) {
    return <div className="h-screen bg-slate-900" />;
  }

  return (
    <div className="h-screen flex flex-col bg-slate-900">
      <TitleBar />

      <div className="flex flex-1 min-h-0">
        <Sidebar
          currentPage={currentPage}
          onPageChange={setCurrentPage}
          bookCount={books.length}
        />

        <main className="flex-1 flex flex-col min-w-0 p-6">
          {currentPage === 'library' && (
            <Library
              books={books}
              setBooks={setBooks}
              sortState={sortState}
              setSortState={setSortState}
            />
          )}
          {currentPage === 'sort' && (
            <SortPanel
              books={books}
              sortState={sortState}
              setSortState={setSortState}
            />
          )}
        </main>
      </div>
    </div>
  );
}
