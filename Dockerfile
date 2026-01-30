# Use the official .NET 9 SDK image to build the app
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy the project file and restore any dependencies (via NuGet)
# Adjust the path if your folder structure is different
COPY ["StravAI.Backend/StravAI.Backend.csproj", "StravAI.Backend/"]
RUN dotnet restore "StravAI.Backend/StravAI.Backend.csproj"

# Copy the rest of the source code
COPY . .
WORKDIR "/src/StravAI.Backend"

# Build the application
RUN dotnet build "StravAI.Backend.csproj" -c Release -o /app/build

# Publish the application to a folder
FROM build AS publish
RUN dotnet publish "StravAI.Backend.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Use the official ASP.NET runtime image for the final stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Copy the published files from the build stage
COPY --from=publish /app/publish .

# Set the environment variable for the port (Koyeb uses PORT=8080 by default)
ENV PORT=8080
EXPOSE 8080

# Start the application
ENTRYPOINT ["dotnet", "StravAI.Backend.dll"]
