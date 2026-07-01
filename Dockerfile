# ANTHILL — container image.
#
# Framework-dependent publish (not self-contained/single-file): the aspnet runtime image
# already provides the .NET runtime, so this avoids the single-file self-extraction quirks
# that can crop up under container filesystems, and keeps rebuilds/pulls smaller since only
# the app layer changes between versions. Self-contained single-file publishing is still the
# right choice for the bare-metal binaries in build.sh/build.ps1 — see README for that path.
#
# The native C++ kernel is intentionally NOT built here. ANTHILL falls back to a bit-identical
# managed implementation automatically when the native library is absent (see
# src/Anthill.Core/Native/NativeKernel.cs), which keeps this image dependency-free (no cmake/g++
# stage). If you want native-kernel acceleration in-container, add a cmake build stage before
# the `build` stage below and copy the resulting .so into native/anthill_kernel/ first.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
# Publishing the Cli project directly (rather than restoring/building via Anthill.sln) restores
# and builds its full ProjectReference chain (Anthill.Core, Anthill.Api) on its own — matching
# what build.sh/build.ps1 already do — so this doesn't depend on every project being registered
# as a top-level entry in the .sln.
RUN dotnet publish src/Anthill.Cli/Anthill.Cli.csproj \
    -c Release \
    -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# curl is only here so HEALTHCHECK below can hit the app's own unauthenticated /health endpoint.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 1000 --create-home --home-dir /app --shell /usr/sbin/nologin anthill

COPY --from=build /app/publish .
COPY config.example.json ./config.example.json

# .anthill/ holds the DB, config.json, logs, backups, exports, and the encryption key —
# everything that must survive a container recreate. ANTHILL creates it (and a sane default
# config.json inside it) on first boot if it isn't already there, so no config file is
# required to start the container — mount a volume here and go.
RUN mkdir -p /app/.anthill && chown -R anthill:anthill /app
USER anthill

# All-interfaces binding is the container-friendly default already (see AnthillConfig.cs);
# these are set explicitly here anyway so `docker inspect` shows the effective values without
# needing to open a shell in the container.
ENV ANTHILL_HOST=0.0.0.0 \
    ANTHILL_PORT=8713 \
    DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8713
VOLUME ["/app/.anthill"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS "http://localhost:${ANTHILL_PORT}/health" || exit 1

ENTRYPOINT ["dotnet", "anthill.dll"]
CMD ["--api"]
