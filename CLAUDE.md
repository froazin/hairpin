# WSL Proxy Relay

A per-user Windows tray app, written in .NET 10, that runs a local HTTP proxy on `127.0.0.1` for WSL2 in mirrored networking mode. WSL traffic then leaves the machine from a Windows process, so endpoint network agents handle it.

## Contents

1. [Layout](#layout)
1. [Conventions](#conventions)
1. [Contributing](#contributing)

## Layout

| Path | Holds |
| --- | --- |
| `WSLProxy.App/` | The executable, `WSLProxyRelay.exe`. It wires the relay and the tray UI into one Generic Host, and ships `appsettings.json` |
| `WSLProxy.Relay/` | The proxy engine, as a class library: listener, HTTP parsing, routing and the relay service. No UI and no Windows-only APIs |
| `WSLProxy.UI/` | The tray icon, flyout and settings window, as a WPF class library |
| `WSLProxy.Relay.Tests/` | MSTest tests of the relay engine |
| `Assets/` | Icons: `wslproxy-logo.ico` is the exe icon, and the black and white `.ico` files are the tray glyphs |
| `build.ps1` | The local build: version, test, publish, sign and checksums |

- Before you change anything, read [Docs/problem.md](Docs/problem.md) for why the app exists.

## Conventions

- Build and test with the commands in [CONTRIBUTING.md](CONTRIBUTING.md#build-and-test). Don't invent others.
- **Dependencies:** in-box .NET APIs first, then Microsoft-published NuGet packages. Ask before you add any other package. Versions live only in `Directory.Packages.props`, and the `packages.lock.json` files are committed.
- **Tests:** keep them few and high value: table-driven tests of pure parsing and matching code, plus a small number of loopback integration tests. Don't test WPF or Win32 interop.
- **Networking:** IPv4 only. The relay listens on `127.0.0.1` only, never on a wildcard address.
- **Logging:** log `host:port`, the route, the status, byte counts and the duration. Never log paths, query strings, headers, bodies or credentials.
- **Security:** treat every byte from a client or upstream as hostile. Parse strictly, enforce the size and time limits, and refuse what you don't understand.
- Warnings are errors, and the recommended .NET analysers run on every build.

## Contributing

| Repository | Path | GitHub | Visibility | Default branch | Kind |
| --- | --- | --- | --- | --- | --- |
| Source | `.` | `froazin/wsl-proxy-relay` | Private, planned to be public | `main` | Code: PRs, squash-merged |

- **Commit scopes:** `relay`, `ui`, `app`, `build`, `docs`, `claude`, `deps`.
- **Feature flags:** none yet.
- **Versions:** GitVersion works out the version from the commit types on `main`, using `GitVersion.yaml`, rather than commitizen. `!` always means a major bump, so until 1.0.0 no commit uses `!`: a breaking change is a `feat`, and its body explains what breaks.
- **Releases:** none yet. CI will tag each release from `main` as `v<version>`. Releases will be Authenticode-signed, published with a `SHA256SUMS.txt` and locked as immutable GitHub releases.
- **CI:** none yet. Build locally with `build.ps1`.
- **User docs:** none published yet. There's no wiki yet.
