import { useState } from 'react';
import TitleBar from './components/TitleBar';
import Sidebar from './components/Sidebar';
import Library from './components/Library';
import SortPanel from './components/SortPanel';

export default function App() {
  const [currentPage, setCurrentPage] = useState('library');
  const [books, setBooks] = useState([]);

  // Sort state lifted here so it persists across page switches
  const [sortState, setSortState] = useState({
    csvPath: '',
    sourcePath: '',
    destPath: '',
    sorting: false,
    progress: null,
    error: null,
  });

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
            <Library books={books} setBooks={setBooks} />
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
