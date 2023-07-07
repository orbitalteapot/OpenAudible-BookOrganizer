# Audio Organizer

Organize your audiobook or music collection!

This is a simple Python script that helps you organize your audio files into a structured directory format based on the metadata of the files. The script uses the `tinytag` library to read metadata from audio files.

## Directory Structure
If the audio is part of a series, the script organizes files in the following structure:

```mathematica
Author
└── Series (Album)
    └── Title
```


If the audio is not part of a series(Album), it organizes files like `this`:

```mathematica
Author
└── Title
```

## Typical Output Example
```mathematica
J.K. Rowling (Artist)
└── Harry Potter (Album)
    ├── Harry Potter and the Sorcerer's Stone.mp3
    ├── Harry Potter and the Chamber of Secrets.mp3
    ├── Harry Potter and the Prisoner of Azkaban.mp3
    ├── Harry Potter and the Goblet of Fire.mp3
    ├── Harry Potter and the Order of the Phoenix.mp3
    ├── Harry Potter and the Half-Blood Prince.mp3
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

### 3. Run the script by executing:
```sh
python3 scripts/organize_audio.py
```
You will be prompted to enter the source folder containing your audio files and the destination folder where you want the organized structure to be created:

```mathematica
Enter the path to the source folder containing audio files: <path_to_source_folder>
Enter the path to the destination folder where you want to organize the audio: <path_to_destination_folder>
```

### 5. The script will organize the audio files based on their metadata.

### 6. Deactivate Environment
```sh
deactivate
```

## Usage With docker
### 1. Clone the repository or download the script.
### 2. Build the Docker Image
```sh
cd audiocollectionsorter
docker build -t audio-organizer .
```

### 3. Run the Container
Run the following command to start a container from the image. Replace /path/to/source with the path to your audio files, and /path/to/destination with the path where you want the organized audio to be stored:
```sh
docker run -it --rm -v /path/to/source:/source -v /path/to/destination:/destination audio-organizer
```

### 5. The container will organize the audio files based on their metadata.
## Note

The script relies on the metadata of the audio files to organize them. Specifically, it uses the `artist` (author), `album` (series), and `title` metadata fields.

Make sure that your audio files have this metadata properly set for the script to work effectively.

The script is copying the files from the source to t
