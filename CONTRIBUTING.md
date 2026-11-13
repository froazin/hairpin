# Contributing

How to report a problem, propose a change, and build and test WSL Proxy Relay. For how the code works, read the design docs in [Docs/](Docs/).

## Contents

1. [Report a problem](#report-a-problem)
1. [Propose a change](#propose-a-change)
1. [Build and test](#build-and-test)
1. [CI](#ci)
1. [Dependencies](#dependencies)
1. [Commits, branches and pull requests](#commits-branches-and-pull-requests)
1. [Versions and releases](#versions-and-releases)

## Report a problem

Open an issue. Say which Windows build, WSL version (`wsl --version`) and networking mode (`wslinfo --networking-mode` inside WSL) you use, and which endpoint network agent is installed. Never paste internal hostnames, URLs, proxy addresses or log lines that contain them into a public issue. Replace them with placeholders such as `proxy.example.com`.

## Propose a change

Open an issue first for anything bigger than a small fix, so the approach can be agreed before you write the code. Then open a pull request (PR) from a branch in your fork.

## Build and test

You need Windows 10 or 11 and the .NET SDK version that [global.json](global.json) names. Everything else comes from NuGet.

| Task | Command |
| --- | --- |
| Build | `dotnet build WSLProxy.slnx` |
| Test | `dotnet test --solution WSLProxy.slnx` |
| Full build: version, test, publish and checksums | `./build.ps1` |
| Full build without tests | `./build.ps1 -SkipTests` |
| Sign the binaries too | `./build.ps1 -CertificateThumbprint <thumbprint> -TimestampUrl <rfc3161-url>` |
| Build a signed MSIX bundle instead of the zips | `./build.ps1 -Msix -CertificateThumbprint <thumbprint> -TimestampUrl <rfc3161-url>` |

`build.ps1` runs in Windows PowerShell 5.1 or PowerShell 7. It writes to `Artifacts/`:

- `Publish/<runtime>/`: a framework-dependent folder for `win-x64` and `win-arm64`, which runs on the .NET 10 Desktop Runtime installed on the machine.
- `WSLProxyRelay-<version>-<runtime>.zip`: each folder, zipped.
- `SHA256SUMS.txt`: a SHA-256 checksum for each zip, in `sha256sum` format.

Signing needs `signtool.exe` from the Windows SDK and a code signing certificate in your certificate store. Without a thumbprint, the script warns and leaves the binaries unsigned.

With `-Msix`, the script writes `WSLProxyRelay-<version>.msixbundle` instead of the zips, and `SHA256SUMS.txt` lists the bundle. It also needs `makeappx.exe` and `makepri.exe` from the Windows SDK, and the certificate's subject becomes the package's publisher. CI only builds the zips. [Docs/releases.md](Docs/releases.md#msix-bundles-built-from-source) describes the MSIX build and how to install the bundle.

### A test certificate for MSIX

To test the MSIX build without a real code signing certificate, make a self-signed one in Windows PowerShell 5.1:

```powershell
$cert = New-SelfSignedCertificate -Type Custom -KeyUsage DigitalSignature -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -Subject "CN=<publisher-name>"
$cert.Thumbprint
Export-Certificate -Cert $cert -FilePath <path>\wslproxy-test.cer
```

- Pass the thumbprint to `build.ps1` with `-CertificateThumbprint`. Signing still needs `-TimestampUrl`, so give it any RFC 3161 timestamp server.
- Don't add the test certificate to a Trusted Root store: that would trust anything signed with its key. `build.ps1` sees that the certificate is self-signed and skips `signtool verify`, which needs a trusted root, with a warning.
- To install the bundle on a test machine, trust the certificate there from an elevated session: `Import-Certificate -FilePath <path>\wslproxy-test.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`.
- Remove the certificate from each store when you've finished testing. Never use a self-signed certificate for a package you give to anyone else.

Windows Application Control (Smart App Control, App Control for Business or AppLocker) can block unsigned executables, and does so unpredictably for new builds (observed). The test project builds no `.exe` for this reason: tests run through the signed `dotnet` host. Releases aren't code-signed yet, so they can be blocked too.

## CI

Each pull request to `main` runs [`.github/workflows/ci.yml`](.github/workflows/ci.yml) on GitHub Actions:

1. `./build.ps1` on Windows Server 2025, in Windows PowerShell 5.1. A pull request that only changes docs or editor and agent settings skips it. Download the zips it builds from the run's `packages` artifact.
1. When the build passes or is skipped, Claude reviews the change against [REVIEW.md](REVIEW.md) and comments on the pull request. Resolve every thread before you merge, with a fix or a reply.

[Docs/ci.md](Docs/ci.md) describes the workflow.

## Dependencies

The app runs in regulated environments, so every dependency has to be justified.

- Use in-box .NET APIs first. Add a NuGet package only when it's clearly worth it, and prefer packages published by Microsoft.
- Set package versions only in [Directory.Packages.props](Directory.Packages.props).
- Commit the `packages.lock.json` files. CI restores in locked mode, so a stale lock file fails the build. Run `dotnet restore WSLProxy.slnx --force-evaluate` after you change a package.
- New versions wait 7 days before they're used. [Dependabot](.github/dependabot.yml) proposes a version only once it's 7 days old (security updates aren't delayed), and [nuget.config](nuget.config) sets `minPublishAgeHours` to 168, so `dotnet package add` and `dotnet package update` skip versions younger than 7 days on NuGet 8.0 and later (.NET SDK 11 and later). Neither stops a restore of a version that's already pinned, so check a package's publish date on nuget.org before you pin it by hand.
- [nuget.config](nuget.config) allows only nuget.org, and only packages that nuget.org has countersigned for a trusted owner. A new package from another owner fails restore with `NU3034` until its owner is added there, and that needs a reviewer's agreement.

## Commits, branches and pull requests

- Commit messages follow [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/), with the scopes listed in [CLAUDE.md](CLAUDE.md#contributing).
- Name branches `feature/<slug>`, `bugfix/<slug>` or `other/<slug>`, in lowercase with hyphens.
- Keep each PR small and focused on one change. PRs are squash-merged, so write the PR title as a commit header and the description as a commit body.
- Rebase on `main` to keep a branch current. Don't merge `main` into it.

## Versions and releases

The version follows [Semantic Versioning](https://semver.org/). GitVersion works it out from the commit types on `main`, using [GitVersion.yaml](GitVersion.yaml):

| Commit type | Bump |
| --- | --- |
| `feat` | Minor |
| `fix`, `perf` or `refactor` | Patch |
| Any type with `!` | Major |
| Anything else, such as `docs`, `ci`, `test` or `chore` | None |

- GitVersion reads the header and every body line, and the biggest bump wins. A body line that starts like a header, such as `feat(ui):`, bumps the version too, so reword it or put it in a bulleted list.
- Types are lowercase and exact: `Feat:` or `feature:` doesn't bump.
- Until the project reaches 1.0.0, don't use `!` or a `BREAKING CHANGE` line: a breaking change is a `feat`, and its body explains what breaks.
- GitVersion needs the full history and tags. In a shallow clone it gives a wrong version without an error, so CI must fetch with `fetch-depth: 0`.
- The release workflow tags each release `v<version>` from `main`, using `MajorMinorPatch`, and publishes the zips with `SHA256SUMS.txt`. There's something to release only when `MajorMinorPatch` differs from `VersionSourceSemVer`. Don't create release tags yourself. [Docs/releases.md](Docs/releases.md) describes the workflow.
- Releases aren't code-signed yet. Check a download against `SHA256SUMS.txt`.
