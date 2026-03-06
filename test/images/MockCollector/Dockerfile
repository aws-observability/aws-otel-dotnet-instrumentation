ARG DOTNET_VERSION=8.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build-env
ARG DOTNET_VERSION
WORKDIR /app
COPY . ./
RUN dotnet publish -c Release -f net${DOTNET_VERSION} -o out

ARG DOTNET_VERSION=8.0
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
EXPOSE 4317 4318
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "MockCollector.dll"]