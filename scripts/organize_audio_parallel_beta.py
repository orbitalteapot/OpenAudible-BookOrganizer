import os
import shutil
from tinytag import TinyTag
import multiprocessing

def move_audio(data):
    file, author, album, title, destination_folder = data

    author_path = os.path.join(destination_folder, author)
    os.makedirs(author_path, exist_ok=True)

    if album:
        series_path = os.path.join(author_path, album)
        os.makedirs(series_path, exist_ok=True)
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
    tasks = []
    for subdir, dirs, files in os.walk(source_folder):
        for file in files:
            file_path = os.path.join(subdir, file)
            audio_file = TinyTag.get(file_path)
            if audio_file is not None and audio_file.artist is not None:
                author = audio_file.artist
                album = audio_file.album
                title = audio_file.title
                if author and title:
                    tasks.append((file_path, author, album, title, destination_folder))
                else:
                    print(f"Metadata is missing for file {file_path}")
            else:
                print(f"Unable to load metadata for file {file_path}")
                
    with multiprocessing.Pool(multiprocessing.cpu_count()/2) as p:
        p.map(move_audio, tasks)

organize_audio("/source", "/destination")
