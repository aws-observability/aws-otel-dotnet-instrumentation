FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG CLOUDWATCH_PLUGIN_VERSION
ARG AWS_INSTRUMENTATION_VERSION
ARG OTEL_VERSION
ARG OTEL_INSTRUMENTATION_VERSION
WORKDIR /src
COPY ./dist/nuget /dist/nuget
COPY ./contract-tests/images/applications/cloudwatch-plugin-otel/nuget.config .
COPY ./contract-tests/images/applications/cloudwatch-plugin-otel/CloudWatchPluginSampleApp.csproj .
COPY ./contract-tests/images/applications/cloudwatch-plugin-otel/Protos ./Protos
RUN dotnet restore "CloudWatchPluginSampleApp.csproj" \
    --configfile nuget.config \
    -p:CloudWatchPluginVersion="${CLOUDWATCH_PLUGIN_VERSION}" \
    -p:AwsInstrumentationVersion="${AWS_INSTRUMENTATION_VERSION}" \
    -p:OpenTelemetryVersion="${OTEL_VERSION}" \
    -p:OpenTelemetryInstrumentationVersion="${OTEL_INSTRUMENTATION_VERSION}"
COPY ./contract-tests/images/applications/cloudwatch-plugin-otel/Program.cs .
COPY ./contract-tests/images/applications/cloudwatch-plugin-otel/AwsSdkInstrumentationPlugin.cs .
RUN dotnet publish "CloudWatchPluginSampleApp.csproj" \
    -c Release \
    --no-restore \
    -p:CloudWatchPluginVersion="${CLOUDWATCH_PLUGIN_VERSION}" \
    -p:AwsInstrumentationVersion="${AWS_INSTRUMENTATION_VERSION}" \
    -p:OpenTelemetryVersion="${OTEL_VERSION}" \
    -p:OpenTelemetryInstrumentationVersion="${OTEL_INSTRUMENTATION_VERSION}" \
    -o /app/publish

FROM base AS final
ARG CLOUDWATCH_PLUGIN_VERSION
ARG OTEL_VERSION
ARG OTEL_AUTO_INSTRUMENTATION_VERSION
WORKDIR /app
COPY --from=build /app/publish .
ARG INSTALL_DIR=/otel-dotnet-auto
RUN mkdir -p ${INSTALL_DIR}
COPY ./dist/UpstreamOpenTelemetryDistribution ${INSTALL_DIR}
LABEL com.amazonaws.cloudwatch-plugin.version="${CLOUDWATCH_PLUGIN_VERSION}"
LABEL com.amazonaws.cloudwatch-plugin.otel-version="${OTEL_VERSION}"
LABEL com.amazonaws.cloudwatch-plugin.otel-auto-version="${OTEL_AUTO_INSTRUMENTATION_VERSION}"
ENV ASPNETCORE_ENVIRONMENT=Development
ENV RESOURCE_DETECTORS_ENABLED=false
ENTRYPOINT ["dotnet", "CloudWatchPluginSampleApp.dll"]
