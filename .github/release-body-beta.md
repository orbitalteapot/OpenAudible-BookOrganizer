> **This is a beta release.** It is published for testing and is not marked as the latest
> release. The Docker `latest` tag still points at the most recent stable version — pull the
> exact version tag to try this build.

A stability-focused release for the book organiser. The sorting engine has been reworked so that
a run is predictable, repeatable, and safe to interrupt.

## New: choose how books are kept up to date

Publishers re-issue audiobooks, and a sort should replace the copy in your organized library rather
than leave the old one behind. Every run already compares each book against the destination and
only writes when they differ; the Sort page now lets you choose *how closely* it compares.

- **Quick** (default, and what previous versions did) — file size plus the first, middle and last
  4 KB. It catches any re-release whose length changed, which is nearly all of them, and costs
  almost nothing to run over a large library.
- **Verify contents** — compares every byte, and replaces any book that changed at all. Slower,
  because it reads both files in full, but it also catches a re-issue that happens to be exactly
  the same size as the copy you already have.

Books that already match are left untouched either way. The progress panel now reports **Updated**
— books replaced because their source had changed — separately from books copied for the first
time. In Docker, set the default with `COMPARISON_MODE=quick|full`; the Sort page still overrides
it for an individual run.

## Fixed since beta.1

**The library list was ordering two columns wrongly.** Sorting by Duration compared the raw text
OpenAudible writes ("45 mins", "9 hrs and 9 mins"), so a 45 minute book was filed *after* a nine
hour one — 45 reads as larger than 9. Sorting by Series compared the series name alone, leaving
every book in a series tied and in whatever order the export happened to use, which routinely put
#10 above #2. Both now sort on what the column means rather than how the value is spelled.

**Linux windows are associated with the installed app.** The `.desktop` entry's name did not match
the window class, so a running window appeared as a separate generic entry rather than grouping
under the app.

The desktop and web interface has also been rebuilt — see below.

## Fixes

**Your library is no longer rewritten by a sort.** Sorting used to overwrite the in-memory book
list with its own cleaned-up values, so the Library view changed after every run and a second run
worked from different data than the first. Sorting is now read-only with respect to your library.

**One author, one folder.** Folder names are now decided once, in list order, before any copying
starts. Previously, parallel workers could each decide independently how to spell an author, and
the same author could end up split across `J.K. Rowling` and `JK Rowling` in the same run.

**Books can no longer overwrite each other.** Two books that resolved to the same file name used
to silently overwrite one another — and if the export had no `Short Title` column, *every* book
was named `Unknown` and only the last one survived. The organiser now falls back through Short
Title, Title and file name, and gives genuinely clashing names a numbered suffix.

**Interrupted runs no longer leave broken files.** Files are copied to a temporary file and then
renamed into place, so cancelling a sort, closing the app, or filling the disk can no longer leave
a half-written book that a later run mistakes for a complete one.

**Failures are reported instead of swallowed.** A missing source folder used to be logged to a
console nobody sees while the UI waited forever for a run that had already given up. Paths are now
validated before a run starts, and errors surface in the app.

**Imports survive imperfect exports.** A single blank rating, an unparseable date, a missing
column or one malformed row used to fail the entire import. Rows are now skipped individually and
reported, and every column is optional. Tab-separated exports and files with a byte-order mark are
read correctly.

**More of your files are found.** Audio files are now located even when the `M4B`/`MP3` columns
are empty, and a `File Paths` entry that records where a file *used to* live — another machine,
another drive letter, a path written on Windows and read on Linux — falls back to the file name
inside the source folder you chose.

**No more empty folder trees.** Author and series folders are only created when there is actually
something to copy into them.

**Desktop app.** A second copy of the app no longer fights the first one for the backend port, and
a backend that fails to start or dies mid-session now says so instead of leaving the window
unresponsive.

**Web/Docker app.** Starting two sorts at once is properly rejected, sorting into your own source
folder is refused, and progress now reports copied, skipped and failed counts separately.

## Behaviour changes worth knowing about

- **File names are now sanitised the same way on every platform.** Characters that are illegal on
  Windows (`: ? * " < > | \ /`) are stripped on Linux and macOS too, so a library stays portable to
  a NAS or an external drive. Existing author and series folders are detected and reused, and an
  existing file whose name differs only by those characters is reused rather than duplicated — but
  if you organised a library on Linux you may still see a small number of renamed files.
- Very long names are truncated to 200 characters, which previously failed the copy outright.
- Names that Windows reserves (`CON`, `NUL`, `COM1`…) are prefixed with `_`.
- Copy concurrency is capped at 8 and can be set with the `OABO_MAX_PARALLELISM` environment
  variable.
- `POST /api/books/parse` now returns `{ books, skippedRows, warnings }` instead of a bare array.
- `POST /api/sort/start` accepts an optional `comparisonMode` of `"quick"` or `"full"`; omitting it
  uses the server default. Sort progress gained an `updatedBooks` count.

## A rebuilt interface

The app has been reworked to stay usable with a large collection and to get out of the way.

- **A 5,000 book library now renders in about a quarter of a second instead of four and a half
  seconds.** Only the rows near the viewport are built, so the browser holds a few hundred elements
  rather than 120,000, and scrolling stays smooth however large the collection is.
- Searching is debounced and no longer re-sorts on every keystroke; the list returns to the top
  when the results change instead of stranding you in the middle of a list that no longer exists.
- The list is a real table with sortable headers, so screen readers can navigate it and it reports
  the true size of your library.
- Narrow windows shed optional columns and collapse the sidebar to icons rather than squeezing
  everything into ellipses.
- Durations read "12h 34m" rather than truncating to "12 hrs and…".
- Flatter, quieter visuals: one accent colour, no gradients or glow, and text that meets the
  WCAG AA contrast minimum on every surface it sits on.

## Testing

178 backend tests cover path safety, name resolution, planning determinism, atomic and idempotent
copying, both update checks, cancellation and CSV robustness. 20 frontend tests cover the search
and ordering rules, including regression tests for the two sorting bugs above. Both suites run in
CI on Linux and Windows, and now gate the release itself.

This beta was also driven by hand against a deliberately hostile library — books with no author,
no series and no duration, a 400 character title, Arabic and Japanese text, emoji, markup in a
title, and duplicate titles — checking that nothing crashed, nothing was written outside the
destination folder, and a second run copied nothing.

## Feedback

Please report anything you hit against this beta on the
[issue tracker](https://github.com/orbitalteapot/OpenAudible-BookOrganizer/issues) — especially
unexpected folder names, files that were not found, or anything that looks renamed after
upgrading.
