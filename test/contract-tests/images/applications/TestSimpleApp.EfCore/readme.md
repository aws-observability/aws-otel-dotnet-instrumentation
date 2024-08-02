# Simple ASP.NET Web API with EntityFrameworkCore & SQLite

A simple web API using SQLite as database and exposing CRUD API

## Option 1: Test the APP using Docker

> Prerequisite: Docker engine installed and configured

Execute the following commands

```sh
docker build -t simpleapp-ef-core . 
docker run -e ASPNETCORE_ENVIRONMENT=Development -p 8080:8080 simpleapp-ef-core
```

After executing these commands, using your browser you can navigate to <http://localhost:8080/swagger>

## Option 2

> Prerequisite: .NET 8 SDK installed and configured

Execute the following commands

```sh
dotnet build && dotnet run
```

After executing these commands, using your browser you can navigate to the URL displayed in your console example <http://localhost:5026/swagger>, the port 5026 might be different, make sure you have the correct port
