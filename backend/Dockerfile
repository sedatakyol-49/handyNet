# syntax=docker/dockerfile:1
# The SDK stage runs natively on the CI machine and cross-publishes for the target (amd64 or arm64, e.g. Oracle
# Ampere); only the small runtime stage is emulated. Avoids running the .NET SDK under QEMU, which is slow and flaky.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props HandyNet.slnx ./
COPY src/ src/
RUN dotnet publish src/HandyNet.Api/HandyNet.Api.csproj -c Release -a $TARGETARCH --self-contained false -o /app --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app .
# Local file storage (product images) when FileStorage:Provider = Local; a named volume inherits this ownership.
RUN mkdir -p /data/files /data/keys && chown $APP_UID /data/files /data/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "HandyNet.Api.dll"]
