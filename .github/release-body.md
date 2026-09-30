Turn a folder full of downloaded audiobooks into a library you can navigate.

OpenAudible Book Organizer reads the CSV export from OpenAudible and copies each book into an
`Author / Series / Book` folder structure, leaving your originals untouched. It runs as a desktop
app on Windows, macOS and Linux, and as a Docker container with a browser interface for a NAS or
home server.

<!--
  Release bodies are rendered outside the repository, so images need absolute URLs. Root-relative
  paths like /images/foo.png silently render as broken links.
-->
![Library](https://raw.githubusercontent.com/orbitalteapot/OpenAudible-BookOrganizer/main/images/app-library.png)

## What's new

### Every book gets a folder of its own

A standalone book used to be copied loose into its author's folder. Audiobookshelf, Plex and similar
servers read a folder holding an audio file as one book, so a single standalone title hid every
series beside it. Now a numbered series book goes in `Series/Book N`, and every other book in a
folder named after its title.

![Before and after](https://raw.githubusercontent.com/orbitalteapot/OpenAudible-BookOrganizer/main/images/folder-layout.png)

**Libraries sorted by an earlier version are tidied up on the next sort.** Loose books are *moved*
into their new folders, not copied a second time, and counted as **Moved**. The organiser now keeps a
small `.openaudible-organizer` file in the destination recording which book is in which folder, so
books keep their folders from one sort to the next. It never writes over a file unless it is provably
that book's own copy: anything else — an old copy of a book you have downloaded again since, say —
is left where it is and named in the problems list, for you to delete if you want.

### Sort automatically

Choose **every 6 or 12 hours, daily or weekly** on the Sort page and new downloads are filed on their
own. On the desktop, the app can keep running in the system tray after you close the window, and
start hidden when you sign in. In Docker, set `SORT_INTERVAL` (for example `6h` or `1d`), or leave it
unset and choose on the page.

![Automatic sorting](https://raw.githubusercontent.com/orbitalteapot/OpenAudible-BookOrganizer/main/images/app-schedule.png)

Automatic sorts never create a missing destination folder, so an unplugged drive or an unmounted
share is never filled from your system disk; they wait and try again instead.

### See every sort, and what went wrong

- Any sort shows in the **Progress** card with a **Cancel** button, whether you started it or the
  schedule did, and a badge in the sidebar follows it from every page.
- Six counters that always add up: **New**, **Updated**, **Moved**, **Up to date**, **Not found**
  and **Failed**.
- A **Problems** list names each book that could not be found or copied, and why, in plain words.
  Nothing points at a log file any more.

### Easier every day

- Your folders and options are remembered, and the library loads by itself when the app opens. The
  Library and Sort pages share one export.
- **Copy speed: Gentle** copies one book at a time, for network drives and USB disks.
- **Light and dark themes**, following your system or chosen in the sidebar.
- If the destination folder does not exist, **Start sorting** asks before creating it.

### Under the hood

- The desktop app's background service now listens only on your own computer.
- In Docker, the folders set by `CSV_PATH`, `SOURCE_PATH` and `DESTINATION_PATH` are enforced, and a
  volume that is not mounted is reported on the page instead of silently filled.

## Install

Download the file for your platform below. Nothing else is required — the app bundles everything
it needs, so you do not need .NET or Node installed.

| Platform | File |
| --- | --- |
| Windows | `...-windows-x64.exe` |
| macOS (Apple Silicon) | `...-macos-arm64.dmg` |
| macOS (Intel) | `...-macos-x64.dmg` |
| Linux | `...-linux-x86_64.AppImage` or `...-linux-amd64.deb` |

These builds are not code-signed, so Windows shows "Windows protected your PC" (**More info** →
**Run anyway**) and macOS says it cannot check the app for malicious software (right-click the app
→ **Open**). The [README](https://github.com/orbitalteapot/OpenAudible-BookOrganizer#these-builds-are-not-code-signed)
has the details.

## Using it

Export your book list from OpenAudible to CSV and choose it on the **Library** page, then pick your
source and destination folders on the **Sort** page and start sorting.

![Sorting](https://raw.githubusercontent.com/orbitalteapot/OpenAudible-BookOrganizer/main/images/app-sort-complete.png)

Books already in place are left alone, so re-running after buying more books is quick.

## Docker

```sh
docker pull ghcr.io/orbitalteapot/openaudible-bookorganizer:latest
```

Note there is no hyphen between "book" and "organizer" in the image name. See the
[README](https://github.com/orbitalteapot/OpenAudible-BookOrganizer#docker-web-app) for the
compose file and the environment variables, including `SORT_INTERVAL`.

## How your books get organised

```text
Dennis E. Taylor/
  Bobiverse/
    Book 1/
      We Are Legion (We Are Bob).m4b
    Book 2/
      For We Are Many.m4b
  The Singularity Trap/
    The Singularity Trap.m4b
Andy Weir/
  Project Hail Mary/
    Project Hail Mary.m4b
```
