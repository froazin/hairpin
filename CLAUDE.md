# WSL Proxy Relay

A per-user Windows tray app, written in .NET 10, that runs a local HTTP proxy on `127.0.0.1` for WSL2 in mirrored networking mode. WSL traffic then leaves the machine from a Windows process, so endpoint network agents handle it.

## Contents

1. [Layout](#layout)
1. [Conventions](#conventions)
1. [Checks](#checks)
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
| `Docs/` | Design docs for engineers changing the code |
| `.github/workflows/ci.yml` | Builds and tests each pull request with `build.ps1`, then Claude reviews it. The release workflow calls it to build what it publishes. Read [Docs/ci.md](Docs/ci.md) before you change it |
| `REVIEW.md` | Instructions for CI's Claude code review: what counts as Important, the Nit cap, what to skip, what to always check and the evidence a finding needs |
| `.github/workflows/release.yml`, `GitVersion.yaml` | On a push to `main`, calls CI, then releases each change that bumps the version as `vX.Y.Z`, with the zips and `SHA256SUMS.txt`. Read [Docs/releases.md](Docs/releases.md) before you change them |

- Before you change anything, read [Docs/problem.md](Docs/problem.md) for why the app exists.
- Before you change `WSLProxy.Relay/` or its tests, read [Docs/relay.md](Docs/relay.md).
- Before you change `WSLProxy.App/` or `WSLProxy.UI/`, read [Docs/tray-ui.md](Docs/tray-ui.md).

## Conventions

- Build and test with the commands in [CONTRIBUTING.md](CONTRIBUTING.md#build-and-test). Don't invent others.
- **Dependencies:** in-box .NET APIs first, then Microsoft-published NuGet packages. Ask before you add any other package. Versions live only in `Directory.Packages.props`, and the `packages.lock.json` files are committed.
- **Tests:** keep them few and high value: table-driven tests of pure parsing and matching code, plus a small number of loopback integration tests. Don't test WPF or Win32 interop.
- **Networking:** IPv4 only. The relay listens on `127.0.0.1` only, never on a wildcard address.
- **Logging:** log `host:port`, the route, the status, byte counts and the duration. Never log paths, query strings, headers, bodies or credentials.
- **Security:** treat every byte from a client or upstream as hostile. Parse strictly, enforce the size and time limits, and refuse what you don't understand.
- Warnings are errors, and the recommended .NET analysers run on every build.

## Checks

Run these before you push. CI runs `build.ps1` in Windows PowerShell 5.1, which does all of them:

```powershell
dotnet build WSLProxy.slnx      # warnings are errors
dotnet test --solution WSLProxy.slnx
./build.ps1                     # version, locked restore, build, test, publish and checksums
```

When you review a change locally, follow `REVIEW.md` as it is on `main` (`git show origin/main:REVIEW.md`), not a copy the change edits. Squash-merge a pull request once CI passes, or didn't run because it only changes docs, and every review thread is resolved. Resolve a thread only after fixing what it raises, or replying with why it isn't a problem.

## Contributing

| Repository | Path | GitHub | Visibility | Default branch | Kind |
| --- | --- | --- | --- | --- | --- |
| Source | `.` | `froazin/wsl-proxy-relay` | Private, planned to be public | `main` | Code: PRs, squash-merged |

- **Commit scopes:** `relay`, `ui`, `app`, `build`, `ci`, `docs`, `claude`, `deps`.
- **Feature flags:** none yet.
- **Versions:** GitVersion works out the version from the commit types on `main`, using `GitVersion.yaml`, rather than commitizen. `!` always means a major bump, so until 1.0.0 no commit uses `!`: a breaking change is a `feat`, and its body explains what breaks.
- **Releases:** the release workflow tags each release from `main` as `v<version>` and publishes the zips with a `SHA256SUMS.txt`. Releases aren't code-signed yet. See [Docs/releases.md](Docs/releases.md).
- **CI:** GitHub Actions, in `.github/workflows/`. See [Docs/ci.md](Docs/ci.md).
- **User docs:** none published yet. There's no wiki yet.
