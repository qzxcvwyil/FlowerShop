# Multi-stage build: SDK image compiles, small ASP.NET runtime image runs the published app.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/FlowerShop.Server -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ConnectionStrings__Default="Data Source=/data/flowershop.db"
VOLUME /data
EXPOSE 8080
# Liveness probe for orchestrators: GET /health (200 OK)
ENTRYPOINT ["dotnet", "FlowerShop.Server.dll"]
