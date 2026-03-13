# OpenAudible Book Organizer

Organize your audiobook collection with a modern desktop app!

A cross-platform Electron application with a C# backend that helps organize your audiobook collection into a structured directory format based on the OpenAudible book list export.

## Screenshots

### Library View
Browse, search, and sort your entire audiobook collection.

![Library](images/bookmanagerapplib.png)

### File Sorter
Configure source and destination paths, then sort your audiobooks into organized folders with real-time progress tracking.

![Sort Files](images/bookmanagerappfilesort.png)

## Getting Started

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Node.js](https://nodejs.org/) (v18+)

### Export Book List from OpenAudible
![export](images/export.png)

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

```
J.K. Rowling (Author)
└── Wizarding World (Series)
    └── Book 1
        └── Harry Potter and the Sorcerer's Stone.mp3
```

## Project Structure

| Folder | Description |
|---|---|
| `AudioFileSorter/` | Shared C# library — CSV parsing, file sorting logic |
| `ManagerApi/` | ASP.NET Core Web API backend |
| `electron-ui/` | Electron + React + Tailwind frontend |
