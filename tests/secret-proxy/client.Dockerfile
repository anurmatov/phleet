# A light stand-in for the agent image when AGENT_IMAGE is not given: the same client tools an
# agent uses against a bound host. CI runs the harness against the real agent image as well.
FROM debian:bookworm-slim
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git gh nodejs python3 openssl jq \
      openssh-client openssh-server \
 && rm -rf /var/lib/apt/lists/*
