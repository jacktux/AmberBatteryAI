FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["AmberBatteryAI.csproj", "./"]
RUN dotnet restore "AmberBatteryAI.csproj"
COPY . .
RUN dotnet publish "AmberBatteryAI.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AmberBatteryAI.dll"]
