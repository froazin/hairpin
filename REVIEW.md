# Review instructions

What CI's Claude code review flags on pull requests (PRs) in this repository, at what severity and what it skips. Rules for all work are in `CLAUDE.md`, and a change that breaks one of them is a finding. This file adds only what's specific to reviews.

Judge the projects as what ships: `WSLProxyRelay.exe` runs at sign-in on machines in regulated environments, relays every proxied connection from WSL, and parses bytes from clients and servers it doesn't trust. Judge the tests, `build.ps1` and the workflows by whether they still check and release the app correctly, not by the standards of the app itself.

## What Important means here

Mark a finding Important only when the change would do one of these:

- Let the relay listen anywhere but `127.0.0.1`, reach a destination the deny list refuses today, or connect to an address its checks didn't approve, such as resolving a host again after it was checked. `Docs/relay.md` describes the rules.
- Weaken HTTP parsing or framing so a message is read differently from how the client or server meant it: accepting what RFC 9110 or 9112 says to reject, mishandling `Transfer-Encoding` or `Content-Length`, forwarding a hop-by-hop field, retrying a request that isn't idempotent, or reusing a server connection for the wrong route.
- Remove or raise a size, time or connection limit without bounding it, or allocate memory or hold a connection in proportion to what a peer sends.
- Log, show or keep a path, query string, header, body or credential, or an exception's message, which can carry them. `CLAUDE.md` says what may be logged.
- Make the app crash, hang or stop relaying on bad settings, a missing file, a registry error, a theme change or an Explorer restart, or block the UI thread on network or file I/O. `Docs/tray-ui.md` says how each is handled.
- Change the start-at-sign-in entry against the user's choice, such as re-enabling an entry the user turned off in Task Manager, or let a Debug build touch it.
- Add IPv6 support or an IPv6 listener: the app is IPv4 only, as `CLAUDE.md` says.
- Add a NuGet package that Microsoft doesn't publish, set a package version outside `Directory.Packages.props`, leave a `packages.lock.json` out of step, or weaken `nuget.config`'s sources, signature validation or trusted signers.
- Make `build.ps1` fail in Windows PowerShell 5.1, or change what it publishes or hashes without updating `Docs/releases.md`.
- Make a design doc, the README, `CLAUDE.md`, `CONTRIBUTING.md` or this file say something about the code that's false after the change, so that someone following it would do the wrong thing.
- Make CI or the release workflow skip a check the change needs, such as a `paths-ignore` entry in `release.yml` that skips a path the build reads, a path filter on `ci.yml`'s pull requests, a path its `changes` job treats as docs that the build reads, or let a job run when one it needs failed. `Docs/ci.md` and `Docs/releases.md` describe how they should work.
- Give a job other than `release` in `release.yml` a token that can write, other than the permissions its `ci` job allows for CI's code review, or let the `release` job check out or run the repository's code, or make it create anything but a `v` tag, such as a GitHub release or packages.
- Widen what the Claude run in CI can do, such as its `permissions`, its `github_token`, its `--allowedTools` or its `--disallowedTools`, past what the "Security" section of `Docs/ci.md` allows, even when the change updates that doc too. Give `CLAUDE_CODE_OAUTH_TOKEN`, or a token that can write, to a step that runs the PR's code, or pass secrets to `ci.yml` from the release workflow.
- Use an action without pinning it to a full commit SHA with its version in a comment, pin one to a release less than 7 days old, or shorten the 7-day Dependabot cooldown.
- Put identifying data into the repository, such as a real hostname, proxy address, internal URL or user path. The repository will be public.

Naming, structure, refactoring ideas, comments and doc wording are Nits at most.

## Pre-existing issues

Don't report an issue that was there before this PR, however serious. It's out of scope for the PR.

## Cap the Nits

Every comment must be resolved before the PR can merge, so report Nits sparingly: at most three per review, and only ones likely to mislead a maintainer or a Claude agent working from the docs. On a review after new commits, report Nits only on code those commits change. CI's code review posts none from the fourth review on a PR, as the "Code review" section of `Docs/ci.md` says.

## Don't report

- Anything the compiler, the .NET analysers (warnings are errors) or the tests report. CI runs them before the review.
- The PR's title and description.
- That releases are only tags, with no GitHub release, packages or signed binaries. It's a decision recorded in `Docs/releases.md`.
- A limit that a design doc's Limits section records, unless the change makes it worse.
- An issue that a resolved review thread on the PR already raises, whatever its replies say. Resolving a thread settles it.

## Always check

These are the rules that no compiler, analyser or test enforces.

### The relay

- Every byte from a client or upstream is treated as hostile: parsed strictly, within the limits in `RelayOptions`, and refused when it isn't understood.
- A new log message uses `[LoggerMessage]` and logs only what `CLAUDE.md` allows.
- A change to routing, parsing, framing or limits comes with a table-driven test or a loopback integration test. A missing test is a Nit.

### The app and UI

- Code that touches WPF objects runs on the UI thread. Relay events arrive on thread-pool threads and are marshalled with the `Dispatcher`.
- Colours and styles come from the Fluent theme's resources, never fixed values, so light, dark and high contrast all work.
- Every Win32 call is one Microsoft documents, and handles, icons and windows it creates are released.
- In-memory stores, such as recent log lines or metrics, are bounded and drop their oldest entries.

### Docs and CI

- A change to what a component does updates its design doc in `Docs/` in the same PR.
- A new path the build reads isn't skipped by `paths-ignore` in `release.yml` or by the `changes` job in `ci.yml`, the two skip the same paths, and `ci.yml` still starts a run on every pull request.
- A change to this file's severities, its Nit cap or what it says about pre-existing issues also changes the code review's prompt in `.github/workflows/ci.yml`, and the other way round.

## Evidence

- Cite a `file:line` in this repository for every claim about behaviour. Don't infer behaviour from a name.
- A review doesn't run the app. Claim that something would fail only when the code, a test or a design doc shows why.
