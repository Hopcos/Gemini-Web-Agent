# ==============================================================
# Build Stage
# ==============================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/GeminiWebAgent/*.csproj src/GeminiWebAgent/
RUN dotnet restore src/GeminiWebAgent/GeminiWebAgent.csproj

COPY src/GeminiWebAgent/ src/GeminiWebAgent/
WORKDIR /src/GeminiWebAgent
RUN dotnet publish GeminiWebAgent.csproj -c Release -o /app/publish /p:UseAppHost=false

# ==============================================================
# Runtime Stage
# ==============================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 5000

ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "GeminiWebAgent.dll"]
