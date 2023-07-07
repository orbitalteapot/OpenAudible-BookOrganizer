# Use the official Python image as the base image
FROM python:3.11-slim

# Set the working directory in the container
WORKDIR /app

# Copy the requirements file into the container
COPY requirements.txt .

# Install the required dependencies
RUN pip install --no-cache-dir -r requirements.txt

# Copy the script into the container
COPY scripts/organize_audio_docker.py .

# Run the script when the container is started
CMD [ "python", "./organize_audio_docker.py" ]
