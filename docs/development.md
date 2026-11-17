# Development

## Build

```bash
# Build the full solution
dotnet build

# Build Docker images from repo root
docker build -t fleet:agent .
docker build -t fleet:orchestrator -f src/Fleet.Orchestrator/Dockerfile .
docker build -t fleet:memory -f src/Fleet.Memory/Dockerfile .
docker build -t fleet:temporal-bridge -f Dockerfile.temporal .
docker build -t fleet:bridge -f src/Fleet.Bridge/Dockerfile .
docker build -t fleet:secrets -f src/Fleet.SecretProxy/Dockerfile .
docker build -t fleet:dashboard -f src/fleet-dashboard/Dockerfile .

# Dashboard dev server
cd src/fleet-dashboard && npm install && npm run dev
```

## Tests

```bash
dotnet test
# With output:
dotnet test --logger "console;verbosity=normal"
```

On macOS a few tests are skipped, each with its reason: the `memory_stats` directory scan is Linux-only by design, and two push-key tests need a curve Apple's cryptography does not have. CI runs them. To run them from a Mac, or to loop a timing-sensitive test on the platform CI uses, run the suite in a Linux container (needs Docker):

```bash
scripts/test-linux.sh                              # the solution, with CI's filter
scripts/test-linux.sh tests/Fleet.Memory.Tests     # one project
FLEET_TEST_CPUS=1 scripts/test-linux.sh tests/Fleet.Comms.Tests --filter "FullyQualifiedName~JournalAttachment"
```

The tests of fleet-memory's Qdrant store (the classes named `*Qdrant*` in `tests/Fleet.Memory.Tests`) need a real Qdrant and are skipped without one, on every platform, each with its reason. To run them (needs Docker):

```bash
bash tests/memory-qdrant/run.sh                         # the release docker-compose.example.yml pins
QDRANT_VERSION=v1.19.1 bash tests/memory-qdrant/run.sh  # another release
```

The script starts one Qdrant container on loopback ports, runs the tests against it, fails if any was skipped, and removes the container. CI runs it for the pinned release and the one before it. To use a Qdrant you already have, set `FLEET_MEMORY_TEST_QDRANT_URL` to its gRPC address (`http://127.0.0.1:6334`); each test class makes and removes a collection of its own.
