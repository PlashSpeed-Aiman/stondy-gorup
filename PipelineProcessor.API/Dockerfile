FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["PipelineProcessor.API/PipelineProcessor.API.csproj", "PipelineProcessor.API/"]
COPY ["PipelineProcessor.Persistence/PipelineProcessor.Persistence.csproj", "PipelineProcessor.Persistence/"]
RUN dotnet restore "PipelineProcessor.API/PipelineProcessor.API.csproj"
COPY . .
WORKDIR "/src/PipelineProcessor.API"
RUN dotnet build "./PipelineProcessor.API.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./PipelineProcessor.API.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PipelineProcessor.API.dll"]
