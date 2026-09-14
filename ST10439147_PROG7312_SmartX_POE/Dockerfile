# Multi-stage: the SDK image builds both the API and the Blazor WebAssembly client,
# the runtime image ships only the published output. The client is served by the API,
# so the whole system is one container on one port.
#
# Build context is this directory (the one holding ST10439147_PROG7312_SmartX_POE.slnx).

# Reference [34]: Docker. Multi-stage builds. Docker Documentation.
#   https://docs.docker.com/build/building/multi-stage/
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

# Known SDK issue (dotnet/aspnetcore#64199): publishing the Server project alone copies the
# Client's *build* wwwroot, whose index.html still has the raw fingerprint placeholder
# (_framework/blazor.webassembly#[.{fingerprint}].js) instead of the real hashed filename.
# Publishing the Client on its own resolves the placeholder correctly, so take just that file.
RUN dotnet publish SmartX.Client/SmartX.Client.csproj -c Release -o /client-publish --no-restore \
    && cp /client-publish/wwwroot/index.html /app/wwwroot/index.html

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
