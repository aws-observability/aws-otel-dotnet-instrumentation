FROM mcr.microsoft.com/dotnet/sdk:10.0.103-alpine3.23
RUN apk update \
    && apk upgrade \
    && apk add --no-cache --update \
        clang20=20.1.8-r1 \
        cmake=4.1.3-r0 \
        make=4.4.1-r3 \
        bash=5.3.3-r1 \
        alpine-sdk=1.1-r0 \
        protobuf=31.1-r1 \
        protobuf-dev=31.1-r1 \
        grpc=1.76.0-r2 \
        grpc-plugins=1.76.0-r2

ENV IsAlpine=true
ENV PROTOBUF_PROTOC=/usr/bin/protoc
ENV gRPC_PluginFullPath=/usr/bin/grpc_csharp_plugin

# Install older sdks using the install script
RUN curl -sSL https://dot.net/v1/dotnet-install.sh --output dotnet-install.sh \
    && chmod +x ./dotnet-install.sh \
    && ./dotnet-install.sh -v 8.0.418 --install-dir /usr/share/dotnet --no-path \
    && ./dotnet-install.sh -v 9.0.311 --install-dir /usr/share/dotnet --no-path \
    && rm dotnet-install.sh

WORKDIR /project