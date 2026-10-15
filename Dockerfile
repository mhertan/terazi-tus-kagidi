# Terazi tuş kağıdı: sayfa + SQLite'ta düzen kaydı (Coolify, Dockerfile build pack)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY sunucu/TeraziSunucu.csproj sunucu/
RUN dotnet restore sunucu/TeraziSunucu.csproj
COPY sunucu/ sunucu/
COPY tus-kagidi.html ./
RUN dotnet publish sunucu/TeraziSunucu.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Veritabanı kalıcı birimde durur; Coolify'da /data için Persistent Storage ekle
RUN mkdir -p /data && chown "$APP_UID" /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    TERAZI_DATA=/data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TeraziSunucu.dll"]
