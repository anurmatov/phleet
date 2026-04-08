# Contributing to Phleet

Thank you for your interest in contributing to Phleet!

## Getting Started

1. Fork the repository and clone your fork.
2. Run `./setup.sh` to set up your local environment. It creates a gitignored `./fleet/` subdir next to the repo holding `.env`, `seed.json`, generated `docker-compose.yml`, workspaces, memories, and credentials.
3. Build the solution: `dotnet build`
4. Run tests: `dotnet test`

## Development Workflow

1. Create a branch from `main` for your change:
   ```bash
   git checkout -b feat/your-feature-name
   ```
2. Make your changes.
3. Ensure all tests pass: `dotnet test`
4. Commit with a clear message following [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat:` — new feature
   - `fix:` — bug fix
   - `refactor:` — code restructuring without behavior change
   - `docs:` — documentation only
   - `test:` — tests only
   - `chore:` — maintenance (deps, config, etc.)
5. Open a pull request against `main`.

## Code Style

- .NET 10 / C# latest features
- File-scoped namespaces
- `required` keyword for mandatory config properties
- `IAsyncEnumerable` for streaming responses
- Microsoft.Extensions.Hosting for service lifecycle
- Options pattern for configuration (no magic strings)
- React + TypeScript + Vite + Tailwind for the dashboard

## Project Structure

| Path | Description |
|------|-------------|
| `src/Fleet.Agent/` | Core agent process |
| `src/Fleet.Orchestrator/` | Agent registry + lifecycle manager |
| `src/Fleet.Temporal/` | Temporal workflow engine bridge |
| `src/Fleet.Bridge/` | RabbitMQ inter-agent relay |
| `src/Fleet.Memory/` | Semantic memory MCP server |
| `src/Fleet.Shared/` | Shared utilities |
| `src/fleet-dashboard/` | React SPA |
| `tests/` | Unit and integration tests |

## Reporting Issues

Please open a GitHub issue with:
- A clear description of the problem or feature request
- Steps to reproduce (for bugs)
- Expected vs actual behavior

## License

By contributing, you agree that your contributions will be licensed under the [MIT License](LICENSE).
