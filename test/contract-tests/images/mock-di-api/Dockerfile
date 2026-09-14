FROM python:3.10
WORKDIR /mock-di-api
COPY . /mock-di-api

# No `pip install`: the server is stdlib-only, so the image builds in seconds.

# Without `-u`, logs are buffered and `wait_for_logs` never sees "Ready".
CMD ["python", "-u", "./mock_di_api_server.py"]
