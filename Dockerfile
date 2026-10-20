# syntax=docker/dockerfile:1

# ---------- Stage 1: build ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Package manifests first: this layer (and the restore below) stays cached
# until a package changes. Directory.*.props are required because of
# Central Package Management.
COPY Directory.Build.props Directory.Packages.props ./
COPY RoomSplit.Api/RoomSplit.Api.csproj RoomSplit.Api/
RUN dotnet restore RoomSplit.Api/RoomSplit.Api.csproj

COPY . .
RUN dotnet publish RoomSplit.Api/RoomSplit.Api.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# ---------- Stage 2: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

# Non-root user provided by the base image (UID 1654).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "RoomSplit.Api.dll"]
