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

Export your book list from OpenAudible to CSV, load it on the **Library** page, then pick your
source and destination folders on the **Sort** page and start sorting.

![Sorting](https://raw.githubusercontent.com/orbitalteapot/OpenAudible-BookOrganizer/main/images/app-sort-complete.png)

Books already in place are skipped, so re-running after buying more books is quick.

## Docker

```sh
docker pull ghcr.io/orbitalteapot/openaudible-bookorganizer:latest
```

Note there is no hyphen between "book" and "organizer" in the image name. See the
[README](https://github.com/orbitalteapot/OpenAudible-BookOrganizer#docker-web-app) for the
compose file and the environment variables.

## How your books get organised

```text
Terry Mancour/
  Spellmonger/
    Book 1/
      Spellmonger.m4b
      Spellmonger.pdf
Andy Weir/
  Project Hail Mary.m4b
```
