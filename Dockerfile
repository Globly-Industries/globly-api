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
# Inside the container listen on all interfaces; only the compose network and 127.0.0.1 on the host can reach it.
ENV ASPNETCORE_ENVIRONMENT=Production \
    Urls=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Globly.Api.dll"]
