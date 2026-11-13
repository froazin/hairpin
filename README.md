<h1 style="font-weight: 600; padding-bottom: 12px; padding-left: 10px;">
    <img src="./Assets/wslproxy-logo.svg" alt="WSLProxy Logo" height="42" style="vertical-align:bottom; margin-right:10px; filter:drop-shadow(2px 5px 3px rgba(15, 7, 36, 0.45));">
    WSL Proxy Relay
    <sub style="font-weight: 400; font-size: 16px;">
        (and systray based GUI)
    </sub>
</h1>
<em style="font-weight: 400; font-size: 14px; color: #888;">
    To make WSL network packets appear as if they are coming from the host system when you're using Mirror networking.
</em>
<br>
<br>

WSL Proxy Relay is a per-user Windows tray app that runs a local HTTP proxy on `127.0.0.1` for WSL2 in mirrored networking mode. WSL traffic sent through it leaves the machine from a Windows process, so endpoint network agents such as Symantec WSS Agent handle it again.

## Why

In mirrored mode, endpoint network agents can't deliver replies to connections that start inside WSL, so they hang. NAT mode fixes that but breaks VPN and internal endpoints. [Docs/problem.md](Docs/problem.md) explains the problem, its cause and the configuration mirrored mode needs.

## Status

Early development. Nothing has been released yet, and user docs will follow in the project's wiki.

## For engineers

- The code is a .NET 10 solution, [WSLProxy.slnx](WSLProxy.slnx), with one folder per project at the root.
- [CONTRIBUTING.md](CONTRIBUTING.md) covers building, testing, dependencies, versions and releases.
- [Docs/ci.md](Docs/ci.md) and [Docs/releases.md](Docs/releases.md): how CI builds and reviews each change, and how releases are made.
- The project is licensed under the [MIT License](LICENSE).
