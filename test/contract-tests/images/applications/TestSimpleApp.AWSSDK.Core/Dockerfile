FROM mcr.microsoft.com/dotnet/sdk:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ./contract-tests/images/applications/TestSimpleApp.AWSSDK.Core .
RUN dotnet restore
RUN dotnet build "TestSimpleApp.AWSSDK.Core.csproj" -c Release -o /app/build
RUN dotnet publish "TestSimpleApp.AWSSDK.Core.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ARG INSTALL_DIR=/opt/aws/otel/dotnet
RUN mkdir -p ${INSTALL_DIR}
COPY ./dist/OpenTelemetryDistribution ${INSTALL_DIR}
RUN mkdir -p /var/log/opentelemetry/dotnet
ENTRYPOINT ["dotnet", "TestSimpleApp.AWSSDK.Core.dll"]

ENV CORECLR_PROFILER_PATH=/opt/aws/otel/dotnet/linux-x64/OpenTelemetry.AutoInstrumentation.Native.so
ENV DOTNET_ADDITIONAL_DEPS=/opt/aws/otel/dotnet/AdditionalDeps
ENV DOTNET_SHARED_STORE=/opt/aws/otel/dotnet/store
ENV DOTNET_STARTUP_HOOKS=/opt/aws/otel/dotnet/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
ENV OTEL_DOTNET_AUTO_HOME=/opt/aws/otel/dotnet
ENV ASPNETCORE_ENVIRONMENT=Development
ENV RESOURCE_DETECTORS_ENABLED=false
