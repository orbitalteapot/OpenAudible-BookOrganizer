import os
import shutil
from tinytag import TinyTag
import csv
import re
import sys

class Book:
    def __init__(self, key, title, author, narrated_by, purchase_date, duration, release_date, ave_rating, genre, series_name, series_sequence, product_id, asin, book_url, summary, description, rating_count, publisher, short_title, copyright, author_url, file_name, series_url, abridged, language, pdf_url, image_url, region, ayce, read_status, user_id, audible_aax, image, m4b=None):
        self.key = key
        self.title = title
        self.author = author
        self.narrated_by = narrated_by
        self.purchase_date = purchase_date
        self.duration = duration
        self.release_date = release_date
        self.ave_rating = ave_rating
        self.genre = genre
        self.series_name = series_name
        self.series_sequence = series_sequence
        self.product_id = product_id
        self.asin = asin
        self.book_url = book_url
        self.summary = summary
        self.description = description
        self.rating_count = rating_count
        self.publisher = publisher
        self.short_title = short_title
        self.copyright = copyright
        self.author_url = author_url
        self.file_name = file_name
        self.series_url = series_url
        self.abridged = abridged
        self.language = language
        self.pdf_url = pdf_url
        self.image_url = image_url
        self.region = region
        self.ayce = ayce
        self.read_status = read_status
        self.user_id = user_id
        self.audible_aax = audible_aax
        self.image = image
        self.m4b = m4b

def sanitize(path):
    return re.sub(r'[\\/*?:"<>|]', ',', path)

def load_books(csv_file):
    books = []
    with open(csv_file, 'r', encoding='utf-8-sig') as file:
        reader = csv.reader(file)
        next(reader)  # Skip header
        for row in reader:
            if len(row) == 34:
                books.append(Book(*row))
            else:
                print(f"Skipping row due to inconsistent data: {row}")
    return books

def move_audio(file, author, album, title, book_number, destination_folder):
    author_path = os.path.join(destination_folder, sanitize(author))
    if not os.path.exists(author_path):
        os.makedirs(author_path)
    
    if album:
        series_path = os.path.join(author_path, sanitize(album))
        if not os.path.exists(series_path):
            os.makedirs(series_path)

        if book_number:
            book_path = os.path.join(series_path, f'Book {book_number}')
            if not os.path.exists(book_path):
                os.makedirs(book_path)
            destination_path = book_path
        else:
            destination_path = series_path

        new_file_name = f'{sanitize(title)}{os.path.splitext(file)[-1]}'
        try:
            shutil.copy(file, os.path.join(destination_path, new_file_name))
        except shutil.SameFileError:
            pass
    else:
        # if no album, put the title directly under the author
        new_file_name = f'{sanitize(title)}{os.path.splitext(file)[-1]}'
        try:
            shutil.copy(file, os.path.join(author_path, new_file_name))
        except shutil.SameFileError:
            pass

def organize_audio(source_folder, csv_file, destination_folder):
    books = load_books(csv_file)
    for subdir, dirs, files in os.walk(source_folder):
        for file in files:
            file_path = os.path.join(subdir, file)
            audio_file = TinyTag.get(file_path)
            if audio_file is not None and audio_file.artist is not None:
                author = sanitize(audio_file.artist)
                album = sanitize(audio_file.album)
                title = sanitize(audio_file.title)
                if title:
                    book_number = None

                    book = next((b for b in books if b.file_name + ".m4b" == file), None)
                    if book:
                        author = sanitize(book.author)
                        if book.series_name:
                            album = sanitize(book.series_name)
                        title = sanitize(book.file_name)
                        if book.series_sequence:
                            book_number = sanitize(book.series_sequence)
                    
                    move_audio(file_path, author, album, title, book_number, destination_folder)
                else:
                    print(f"Metadata is missing for file {file_path}")
            else:
                print(f"Unable to load metadata for file {file_path}")

try:
    print("The script will only take .m4b file format at the moment")
    source_folder = input("Enter the full path to the source folder containing audio files: ")
    csv_file = input("Enter the full path to the openaudible book export file: ")
    destination_folder = input("Enter the full path to the destination folder where you want the organized copy of your audiobooks: ")
    organize_audio(source_folder, csv_file, destination_folder)
except Exception as e:
    print(f"Unexpected error: {e}")
    sys.exit(1)



