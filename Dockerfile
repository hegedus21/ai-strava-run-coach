
# Use the official .NET 9 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
RUN apt-get update && apt-get install -y --no-install-recommends clang zlib1g-dev
WORKDIR /src

# Copy ONLY the project file first to leverage layer caching
COPY ["StravAI.Backend/StravAI.Backend.csproj", "StravAI.Backend/"]
RUN dotnet restore "StravAI.Backend/StravAI.Backend.csproj"

# Copy ONLY the backend source code
COPY StravAI.Backend/ StravAI.Backend/
WORKDIR "/src/StravAI.Backend"

# Build the application
RUN dotnet build "StravAI.Backend.csproj" -c Release -o /app/build

# Publish the application using Native AOT in a dedicated stage
FROM build AS publish
RUN dotnet publish "StravAI.Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Use the lightweight runtime-deps image for the final stage
FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS final
WORKDIR /app
# Copy from the 'publish' stage defined above
COPY --from=publish /app/publish .

# Set the environment variable for the port
ENV PORT=8080
EXPOSE 8080

# Start the application
ENTRYPOINT ["./StravAI.Backend"]
