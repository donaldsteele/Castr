# Image for the multi-container end-to-end fan-out tests.
#
# The build context is the self-contained linux-x64 publish output of Castr.Cli
# (see CastrImageFixture): it contains the single-file `castr` binary plus the
# NSec-required native `libsodium.so`. We add iproute2 so tests can inject real
# kernel-level packet loss with `tc qdisc ... netem` (needs --cap-add=NET_ADMIN),
# and coreutils for `sha256sum` (byte-identity assertion).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

RUN apt-get update \
    && apt-get install -y --no-install-recommends iproute2 coreutils \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/castr
COPY castr ./castr
COPY libsodium.so ./libsodium.so
RUN chmod +x ./castr && ln -s /opt/castr/castr /usr/local/bin/castr

ENTRYPOINT ["/opt/castr/castr"]
