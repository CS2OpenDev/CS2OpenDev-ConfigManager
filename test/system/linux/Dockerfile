# A Linux "player PC" for acb: a non-root user whose home holds a fake Steam where a real one would be.
# Build context: a folder holding acb (the published linux-x64 binary), make-fake-steam.sh and scenarios.sh; CI
# assembles it (.github/workflows/ci.yml, job "linux-pc").
#   docker build -t acb-linux-pc -f test/system/linux/Dockerfile <context>
#   docker run --rm acb-linux-pc
# Nothing from Valve is in the image; the fixture writes Steam's layout at run time, so every run starts clean.
FROM ubuntu:24.04

# acb is Native AOT with invariant globalization: it needs libc and nothing else.
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# A process named cs2 for the "CS2 running" row: acb matches by process name only.
RUN cp /bin/sleep /usr/local/bin/cs2

COPY acb /opt/acb/acb
COPY make-fake-steam.sh scenarios.sh /opt/acb-fixture/
RUN chmod 0755 /opt/acb/acb /opt/acb-fixture/*.sh

# The scenarios rewrite Steam folders in the home; this home is thrown away with the container.
ENV ACB_DISPOSABLE_MACHINE=1

# ubuntu:24.04 ships a uid 1000 "ubuntu" user; make our own so the name is predictable.
RUN useradd --create-home --uid 1001 --shell /bin/bash player
USER player
WORKDIR /home/player
ENTRYPOINT ["bash", "/opt/acb-fixture/scenarios.sh"]
