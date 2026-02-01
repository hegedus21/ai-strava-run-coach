
# Use the official .NET 9 SDK image to build the app
# Note: Native AOT requires clang and zlib
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev
WORKDIR /src

# Copy ONLY the project file first to leverage layer caching
COPY ["StravAI.Backend/StravAI.Backend.csproj", "StravAI.Backend/"]
RUN dotnet restore "StravAI.Backend/StravAI.Backend.csproj"

# Copy ONLY the backend source code (Prevents uploading frontend node_modules)
COPY StravAI.Backend/ StravAI.Backend/
WORKDIR "/src/StravAI.Backend"

# Publish the application using Native AOT
RUN dotnet publish "StravAI.Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Use the lightweight 'distroless' or alpine-based runtime for smallest size
# For AOT, we don't even need the full ASP.NET runtime, just dependencies
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Set the environment variable for the port
ENV PORT=8080
EXPOSE 8080

# Start the application
ENTRYPOINT ["./StravAI.Backend"]
