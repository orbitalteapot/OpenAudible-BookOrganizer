# OpenAudible Book Organizer

Organize your audiobook collection with a modern desktop app.

OpenAudible Book Organizer is a cross-platform Electron application with a C# backend that helps organize your audiobook collection into a structured directory format based on the OpenAudible book list export.

Now with Windows, Linux, macOS, and Docker support.

## Screenshots

### Library View
Browse, search, and sort your entire audiobook collection.

![Library View](images/bookmanagerapplib.png)

### Library with Books Loaded
See your imported library populated and ready to browse, filter, and manage.

![Loaded Books](images/loadedbooks.png)

### File Sorter
Configure source and destination paths, then sort your audiobooks into organized folders with real-time progress tracking.

![File Sorter](images/bookmanagerappfilesort.png)

### Export Book List from OpenAudible
Export your OpenAudible library, then use that export to organize your books automatically.

![OpenAudible Export](images/export.png)

### Start Sorting
Select your OpenAudible export file, choose your source and destination directories, and let the app handle the rest. Your audiobooks will be neatly organized into folders by author and series.

![Start Sorting](images/sorting.png)

## Getting Started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) (v18+)

### Run in Development Mode
```sh
# Start the C# API backend
dotnet run --project ManagerApi

# In a separate terminal, start the Electron app
cd electron-ui
npm install
npm run dev
```

### Build Installers
```sh
cd electron-ui

# Windows (NSIS installer)
npm run dist:win

# Linux (AppImage + deb)
npm run dist:linux

# macOS x64 (dmg)
npm run dist:mac-x64

# macOS Apple Silicon (dmg)
npm run dist:mac-arm
```

Installers are output to `electron-ui/release/`.

## Directory Structure

Books are organized into the following folder structure:

```text
J.K. Rowling (Author)
\-- Wizarding World (Series)
    +-- Book 1
        \-- Harry Potter and the Sorcerer's Stone.mp3
```

## Project Structure

| Folder | Description |
|---|---|
| `AudioFileSorter/` | Shared C# library for CSV parsing and file sorting logic |
| `ManagerApi/` | ASP.NET Core Web API backend |
| `electron-ui/` | Electron + React + Tailwind frontend |
