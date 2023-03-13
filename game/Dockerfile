FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
WORKDIR /game

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /game
COPY . .
RUN dotnet restore game.csproj
RUN dotnet build "./game.csproj" -c Release -o /out

FROM build AS publish
RUN dotnet publish game.csproj -c Release -o /out

# Building final image used in running container
FROM base AS final
# Installing procps on the container to enable debugging of .NET Core
# RUN apt-get update && apt-get install -y unzip procps
WORKDIR /game
COPY --from=publish /out .

ENTRYPOINT ["dotnet", "game.dll"]
