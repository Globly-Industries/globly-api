# syntax=docker/dockerfile:1.7
# Globly API image. Build from the repo root: docker compose --profile prod up -d --build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Globly.Api/Globly.Api.csproj src/Globly.Api/
RUN dotnet restore src/Globly.Api/Globly.Api.csproj
COPY src/ src/
RUN dotnet publish src/Globly.Api/Globly.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# ffmpeg strips metadata from uploaded video and audio; libgomp1 is needed by Magick.NET (images).
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg libgomp1 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
# NSFW image classifier (Moderation/NsfwClassifier.cs): an ONNX export of Falconsai/nsfw_image_detection
# (Apache-2.0), pinned to a revision and verified. Readable by the non-root app user.
# The directory is made first: if ADD creates it, BuildKit gives it the --chmod mode too (644, no
# execute bit), and the app user can't open the file inside (moby/buildkit#5943).
RUN mkdir -m 755 models
ADD --checksum=sha256:d9afb1e057104e6cc8616d174f0f6a8b8b0389c839eea7d5efa4bdf1a77efd27 --chmod=644 \
    https://huggingface.co/onnx-community/nsfw_image_detection-ONNX/resolve/1ceb3c7fe1e9f3f2507e6df577437f23a9149fd5/onnx/model_quantized.onnx \
    models/nsfw.onnx
# Inside the container listen on all interfaces; only the compose network and 127.0.0.1 on the host can reach it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    Urls=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Globly.Api.dll"]
