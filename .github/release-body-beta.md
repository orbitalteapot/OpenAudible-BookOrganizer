> **This is a beta release.** It is published for testing and is not marked as the latest
> release. The Docker `latest` tag still points at the most recent stable version — pull the
> exact version tag to try this build.

A stability-focused release for the book organiser. The sorting engine has been reworked so that
a run is predictable, repeatable, and safe to interrupt.

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
are empty, and Windows-style paths in `File Paths` are matched on Linux and macOS.

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

## Testing

This release adds a test suite (136 tests) covering path safety, name resolution, planning
determinism, atomic and idempotent copying, cancellation, and CSV robustness, plus a CI workflow
that runs it on Linux and Windows.

## Feedback

Please report anything you hit against this beta on the
[issue tracker](https://github.com/orbitalteapot/OpenAudible-BookOrganizer/issues) — especially
unexpected folder names, files that were not found, or anything that looks renamed after
upgrading.
