FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution-level files first for layer caching.
COPY global.json MemoryService.slnx ./
COPY src/MemoryService.Api/MemoryService.Api.csproj          src/MemoryService.Api/
COPY src/MemoryService.Core/MemoryService.Core.csproj        src/MemoryService.Core/
COPY src/MemoryService.Infrastructure/MemoryService.Infrastructure.csproj src/MemoryService.Infrastructure/
COPY src/MemoryService.Llm/MemoryService.Llm.csproj          src/MemoryService.Llm/
COPY src/MemoryService.Recall/MemoryService.Recall.csproj    src/MemoryService.Recall/

RUN dotnet restore src/MemoryService.Api/MemoryService.Api.csproj

COPY src/ src/
RUN dotnet publish src/MemoryService.Api/MemoryService.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "MemoryService.Api.dll"]
