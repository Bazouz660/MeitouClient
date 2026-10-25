# Meitou play reports. Built from the repository root (it needs Directory.Build.props):
#   docker build -f services/Meitou.Reports/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Build.targets ./
COPY services/Meitou.Reports/Meitou.Reports.csproj services/Meitou.Reports/
RUN dotnet restore services/Meitou.Reports/Meitou.Reports.csproj
COPY services/Meitou.Reports/ services/Meitou.Reports/
RUN dotnet publish services/Meitou.Reports/Meitou.Reports.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "meitou-reports.dll"]
