FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine
RUN apk update \
    && apk upgrade \
    && apk add --no-cache --update \
        clang \
        cmake \
        make \
        bash \
        alpine-sdk \
        protobuf \
        protobuf-dev \
        grpc \
        grpc-plugins

ENV IsAlpine=true
ENV PROTOBUF_PROTOC=/usr/bin/protoc
ENV gRPC_PluginFullPath=/usr/bin/grpc_csharp_plugin

# Install older sdks using the install script
RUN curl -sSL https://dot.net/v1/dotnet-install.sh --output dotnet-install.sh \
    && chmod +x ./dotnet-install.sh \
    && ./dotnet-install.sh --channel 8.0 --install-dir /usr/share/dotnet --no-path \
    && ./dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet --no-path \
    && rm dotnet-install.sh

WORKDIR /project