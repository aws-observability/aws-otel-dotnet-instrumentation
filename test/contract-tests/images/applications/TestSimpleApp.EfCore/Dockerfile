#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.
# Meant to be run from aws-otel-dotnet-instrumentation/test

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ./contract-tests/images/applications/TestSimpleApp.EfCore .
RUN dotnet build "TestSimpleApp.EfCore.csproj" -c Release -o /app/build
RUN dotnet publish "TestSimpleApp.EfCore.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ARG INSTALL_DIR=/opt/aws/otel/dotnet
RUN mkdir -p ${INSTALL_DIR}
COPY ./dist/OpenTelemetryDistribution ${INSTALL_DIR}
RUN mkdir -p /var/log/opentelemetry/dotnet
ENTRYPOINT ["dotnet", "TestSimpleApp.EfCore.dll"]

ENV CORECLR_PROFILER_PATH=/opt/aws/otel/dotnet/linux-x64/OpenTelemetry.AutoInstrumentation.Native.so
ENV DOTNET_ADDITIONAL_DEPS=/opt/aws/otel/dotnet/AdditionalDeps
ENV DOTNET_SHARED_STORE=/opt/aws/otel/dotnet/store
ENV DOTNET_STARTUP_HOOKS=/opt/aws/otel/dotnet/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
ENV OTEL_DOTNET_AUTO_HOME=/opt/aws/otel/dotnet