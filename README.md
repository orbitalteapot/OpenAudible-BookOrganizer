# Audio Organizer

Organize your audiobook or music collection!

[![Push to DockerHub audiosort latest](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiosort-latest.yml/badge.svg)](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiosort-latest.yml)
[![Push to DockerHub latest](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiobooksort-latest.yml/badge.svg)](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiobooksort-latest.yml)
[![Push to DockerHub audiobooksort taged](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiobooksort.yml/badge.svg)](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiobooksort.yml)
[![Push to DockerHub audiosort taged](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiosort.yml/badge.svg)](https://github.com/orbitalteapot/audiocollectionsorter/actions/workflows/docker-audiosort.yml)

This is a simple Python script that helps you organize your audio files into a structured directory format based on the metadata of the files. The script uses the `tinytag` library to read metadata from audio files.

## Directory Structure
If the audio is part of a series, the script organizes files in the following structure for audio:

```mathematica
Author
└──(Album)
    └── Title
```


If the audio is not part of a series(Album), it organizes files like `this`:

```mathematica
Author
└── Title
```

## Typical Output Example For AudioBooks
```mathematica
J.K. Rowling (Artist)
└── Harry Potter (Album)
    ├── Book 1
    │   └── Harry Potter and the Sorcerer's Stone.mp3
    ├── Book 2
    │   └── Harry Potter and the Chamber of Secrets.mp3
    ├── Book 3
    │   └── Harry Potter and the Prisoner of Azkaban.mp3
    ├── Book 4
    │   └── Harry Potter and the Goblet of Fire.mp3
    ├── Book 5
    │   └── Harry Potter and the Order of the Phoenix.mp3
    ├── Book 6
    │   └── Harry Potter and the Half-Blood Prince.mp3
    └── Book 7
        └── Harry Potter and the Deathly Hallows.mp3

```

## Requirements

- Python 3.10+
- tinytag

## Usage Without docker

### 1. Clone the repository or download the script.

### 2. Install the required Python library by running:

```sh
cd audiocollectionsorter
python3 -m venv .
source ./bin/activate
pip install -r requirements.txt
```

### 3. Run the script according to your needs by executing:
```sh
python3 scripts/organize_audio.py
```
or
```sh
python3 scripts/organize_audiobook.py
```
You will be prompted to enter the source folder containing your audio files and the destination folder where you want the organized structure to be created:

```mathematica
Enter the path to the source folder containing audio files: <path_to_source_folder>
Enter the path to the destination folder where you want to organize the audio: <path_to_destination_folder>
```

### 4. The script will organize the audio files based on their metadata.

### 5. Deactivate Environment
```sh
deactivate
```

## Usage With docker
### 1. Clone the repository or download the script.
### 2. Build the Docker Image for sorting audio or audiobooks
```sh
cd audiocollectionsorter
docker build -t audiosorter -f AudioSort_Dockerfile .
docker build -t audiobooksorter -f AudioBookSort_Dockerfile .
```

### 3. Run the Container
Run the following command to start a container from the image. Replace /path/to/source with the path to your audio files, and /path/to/destination with the path where you want the organized audio to be stored:
```sh
docker run -it --rm -v /path/to/source:/source -v /path/to/destination:/destination audiosorter
docker run -it --rm -v /path/to/source:/source -v /path/to/destination:/destination audiobooksorter
```

### 4. The container will organize the audio files based on their metadata.

## Create the executables for windows
You can run the following commands
```sh
cd audiocollectionsorter
python3 -m venv myenv
./Script/activate
pip install -r requirements.txt
pyinstaller .\scripts\organize_audio.py --onefile
pyinstaller .\scripts\organize_audiobook.py --onefile
deactivate
```
This will produce two programs `organize_audio.exe` and `organize_audiobook.exe` and can be used as normal. If you don't want to do this i will make the files available under release.

## Alternativly you can pull the images from DockerHub
```sh
docker pull orbitalteapot/audiobookcollectionsorter
docker pull orbitalteapot/audiocollectionsorter
```
## Note

The script relies on the metadata of the audio files to organize them. Specifically, it uses the `artist` (author), `album` (series), and `title` metadata fields.

Make sure that your audio files have this metadata properly set for the script to work effectively.

The script copies the files from the source to the destination, so no data will be lost if something goes wrong.
