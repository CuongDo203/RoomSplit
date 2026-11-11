# syntax=docker/dockerfile:1

# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src

# Package manifests first: this layer (and the restore below) stays cached
# until a package changes. Directory.*.props are required because of
# Central Package Management.
COPY Directory.Build.props Directory.Packages.props ./
COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/
RUN dotnet restore RoomSplit.Api/RoomSplit.Api.csproj

COPY . .
# Commit SHA from CI (--build-arg GIT_SHA=...). .git is not in the build context, so the SDK
# cannot read it itself; it appends SourceRevisionId to AssemblyInformationalVersion
# ("1.0.0+<sha>"), which GET /version reads. Declared after restore to keep that layer cached.
ARG GIT_SHA=unknown
RUN dotnet publish RoomSplit.Api/RoomSplit.Api.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false \
    /p:SourceRevisionId=${GIT_SHA}

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS final
# ARGs are per stage: declare again to label the image (docker inspect shows the commit).
ARG GIT_SHA=unknown
LABEL org.opencontainers.image.revision=${GIT_SHA}
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user provided by the base image (UID 1654).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "RoomSplit.Api.dll"]
