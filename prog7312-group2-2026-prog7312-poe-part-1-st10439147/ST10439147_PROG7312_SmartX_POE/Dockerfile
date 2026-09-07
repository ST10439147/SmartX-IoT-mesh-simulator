# Multi-stage: the SDK image builds both the API and the Blazor WebAssembly client,
# the runtime image ships only the published output. The client is served by the API,
# so the whole system is one container on one port.
#
# Build context is this directory (the one holding ST10439147_PROG7312_SmartX_POE.slnx).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first so `dotnet restore` is cached and only re-runs when a
# csproj changes, not on every source edit.
COPY ST10439147_PROG7312_SmartX_POE.slnx ./
COPY SmartX.Shared/SmartX.Shared.csproj                 SmartX.Shared/
COPY ST10439147_PROG7312_SmartX_POE/SmartX.API.csproj   ST10439147_PROG7312_SmartX_POE/
COPY SmartX.Client/SmartX.Client.csproj                 SmartX.Client/
RUN dotnet restore ST10439147_PROG7312_SmartX_POE/SmartX.API.csproj

COPY . .

# Fail the build if the logic is broken, rather than shipping a broken image.
RUN dotnet run --project ST10439147_PROG7312_SmartX_POE --no-launch-profile -- --selftest
RUN dotnet publish ST10439147_PROG7312_SmartX_POE/SmartX.API.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

# Encrypted attachments and the generated key live here. Mount a volume over it to
# keep uploads across container restarts.
RUN mkdir -p /app/storage/attachments && chown -R $APP_UID:$APP_UID /app/storage
USER $APP_UID

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
