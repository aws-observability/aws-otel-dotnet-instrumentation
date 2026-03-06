ARG DOTNET_VERSION=8.0
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build-env
ARG DOTNET_VERSION
WORKDIR /app
COPY . ./
RUN dotnet publish *.csproj -c Release -f net${DOTNET_VERSION} -o out

ARG DOTNET_VERSION=8.0
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /app
COPY . ./
ENV AWS_REGION=us-west-2
ENV OTEL_EXPORTER_OTLP_ENDPOINT=http://otel:4317
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "dotnet-sample-app.dll"]
  
