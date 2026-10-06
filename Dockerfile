# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first (cached until the project file or lock file changes). The lock file pins every package.
COPY Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj Pixelbadger.Toolkit.Rag/packages.lock.json Pixelbadger.Toolkit.Rag/
RUN dotnet restore Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj --locked-mode

COPY Pixelbadger.Toolkit.Rag/ Pixelbadger.Toolkit.Rag/
RUN dotnet publish Pixelbadger.Toolkit.Rag/Pixelbadger.Toolkit.Rag.csproj \
    --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# ffmpeg decodes audio for ingestion.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# The EmbeddingGemma 2 ONNX snapshot is NOT part of the image (the service never downloads models):
# mount it read-only at /models/embeddinggemma-2-onnx. The Lucene index lives on the /data volume.
# The SQL Server connection string is supplied at run time (Rag__ConnectionString).
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Rag__IndexPath=/data/index \
    Rag__ModelPath=/models/embeddinggemma-2-onnx

RUN mkdir -p /data/index && chown -R app:app /data
VOLUME /data
USER app
EXPOSE 8080

ENTRYPOINT ["dotnet", "Pixelbadger.Toolkit.Rag.dll"]
