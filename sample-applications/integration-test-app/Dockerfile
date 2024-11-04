FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /app
COPY . ./
RUN dotnet publish integration-test-app/*.csproj -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
EXPOSE 8080
COPY --from=build-env /app/out .
ENTRYPOINT ["dotnet", "integration-test-app.dll"]

ARG INSTALL_DIR=/opt/aws/otel/dotnet
RUN mkdir -p ${INSTALL_DIR}
RUN ls ../../
COPY ./OpenTelemetryDistribution/ ${INSTALL_DIR}
RUN mkdir -p /var/log/opentelemetry/dotnet

ENV CORECLR_ENABLE_PROFILING=1
ENV CORECLR_PROFILER={918728DD-259F-4A6A-AC2B-B85E1B658318}
ENV CORECLR_PROFILER_PATH=${INSTALL_DIR}/linux-arm64/OpenTelemetry.AutoInstrumentation.Native.so
ENV DOTNET_ADDITIONAL_DEPS=${INSTALL_DIR}/AdditionalDeps
ENV DOTNET_SHARED_STORE=${INSTALL_DIR}/store
ENV DOTNET_STARTUP_HOOKS=${INSTALL_DIR}/net/OpenTelemetry.AutoInstrumentation.StartupHook.dll
ENV OTEL_DOTNET_AUTO_HOME=${INSTALL_DIR}
ENV OTEL_DOTNET_AUTO_PLUGINS="AWS.Distro.OpenTelemetry.AutoInstrumentation.Plugin, AWS.Distro.OpenTelemetry.AutoInstrumentation"
ENV OTEL_AWS_APPLICATION_SIGNALS_ENABLED="true"
ENV OTEL_TRACES_SAMPLER="always_on"
ENV OTEL_EXPORTER_OTLP_PROTOCOL="http/protobuf"
ENV OTEL_AWS_APPLICATION_SIGNALS_EXPORTER_ENDPOINT="http://otel:4318/v1/metrics"
