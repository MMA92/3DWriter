#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"

echo "Creating cli"
podman run --rm -v "$PWD":/src:Z -w /src/3DWriterCli mcr.microsoft.com/dotnet/sdk:8.0 dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /src/dist
echo "Done"

echo "Creating gui"
podman run --rm -v "$PWD":/src:Z -w /src/3DWriterGui mcr.microsoft.com/dotnet/sdk:8.0 dotnet publish -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o /src/dist-gui
echo "Done"

echo "cleaning up"
rm -rf 3DWriterCli/bin 3DWriterCli/obj 3DWriterCore/bin 3DWriterCore/obj \
       3DWriterGui/bin 3DWriterGui/obj 3DWriterGui/.avalonia-build-tasks
echo "Done"

echo "launching gui"
echo "./dist-gui/3DWriterGui"
./dist-gui/3DWriterGui
