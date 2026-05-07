FROM python:3.12-slim

WORKDIR /app

# cognee pulls in a number of native deps for its KG / NLP layers.
RUN apt-get update && apt-get install -y --no-install-recommends \
    build-essential libffi-dev libssl-dev && \
    rm -rf /var/lib/apt/lists/*

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY main.py .

ENV PYTHONUNBUFFERED=1
# cognee's local data lives here; mount a volume so it survives restarts.
ENV COGNEE_DATA_PATH=/data
RUN mkdir -p /data

EXPOSE 8082

CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8082"]
