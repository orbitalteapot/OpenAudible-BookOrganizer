import os
import shutil
from tinytag import TinyTag
import re

def move_audio(file, author, album, title, destination_folder):
    author_path = os.path.join(destination_folder, author)
    if not os.path.exists(author_path):
        os.makedirs(author_path)
    
    if album:
        series_path = os.path.join(author_path, album)
        if not os.path.exists(series_path):
            os.makedirs(series_path)
        new_file_name = f'{title}{os.path.splitext(file)[-1]}'
        try:
            shutil.copy(file, os.path.join(series_path, new_file_name))
        except shutil.SameFileError:
            pass
    else:
        new_file_name = f'{title}{os.path.splitext(file)[-1]}'
        try:
            shutil.copy(file, os.path.join(author_path, new_file_name))
        except shutil.SameFileError:
            pass

def organize_audio(source_folder, destination_folder):
    for subdir, dirs, files in os.walk(source_folder):
        for file in files:
            file_path = os.path.join(subdir, file)
            audio_file = TinyTag.get(file_path)
            if audio_file is not None and audio_file.artist is not None:
                author = audio_file.artist
                album = audio_file.album
                title = audio_file.title
                if album and title:
                    book_number = None

                    if album:
                        # Check for both delimiters and "Book <int>"
                        match = re.search(r': (.*), Book (\d+)', album)
                        if match:
                            album = match.group(1)
                            book_number = int(match.group(2))
                        else:
                            # Check for only ':' delimiter
                            match = re.search(r'(.*): (.*)', album)
                            if match:
                                title = match.group(1)
                                album = match.group(2)
                    move_audio(file_path, author, album, title, destination_folder)
                else:
                    print(f"Metadata is missing for file {file_path}")
            else:
                print(f"Unable to load metadata for file {file_path}")

source_folder = input("Enter the path to the source folder containing audio files: ")
destination_folder = input("Enter the path to the destination folder where you want to organize the audio: ")
organize_audio(source_folder, destination_folder)
