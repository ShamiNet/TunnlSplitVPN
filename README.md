# TunnlSplitVPN

A Windows WPF split-tunnel frontend for WireSock Secure Connect / WireSock VPN Client.

## Run

```powershell
dotnet run
```

Import a real WireGuard `.conf` file before connecting. The application validates the
interface key and addresses and each peer's public key, endpoint, and allowed IPs.
It does not create sample tunnels or insert default keys, DNS servers, or routes.

WireSock must be installed separately. The application checks common install paths
and `PATH`, or you can choose `wiresock-client.exe` with **Locate WireSock**. Starting
a tunnel requires administrator privileges; the application offers to relaunch itself
elevated. It runs the client as a process using the documented `run -config <path>
-log-level info` command and captures stdout and stderr asynchronously.

Per-application rules are written as WireSock-specific `#@ws:AllowedApps` and
`#@ws:DisallowedApps` directives in each peer section. This keeps the runtime
configuration compatible with standard WireGuard parsers. The original imported
configuration is retained, and generated runtime files and settings are stored under
`%LOCALAPPDATA%\TunnlSplitVPN`. The settings file contains rules and paths, not tunnel
keys; runtime `.conf` files necessarily contain the tunnel's private key and are
removed after the owned client process exits.

The UI reports when the WireSock process is running but does not claim that the VPN
handshake or traffic has been verified. It only stops the WireSock process it started;
it does not stop WireSock services or other client processes. Changing tunnel rules
restarts that owned process.

Closing the window keeps the application and any active tunnel running in the Windows
notification area. Double-click the tray icon or choose **Open TunnlSplitVPN** to show
the window again. Choose **Exit** from the tray icon menu to stop the application-owned
tunnel and exit completely.

## Build and tests

```powershell
dotnet build
dotnet test tests\TunnlSplitVPN.Tests.csproj
```

## Create a Windows installer

Install Inno Setup 6 or 7, then run this from the project directory in PowerShell:

```powershell
.\installer\Build-Installer.ps1
```

This publishes the x64 WPF app and builds a per-user Windows setup executable with
Inno Setup. The installer is written to
`artifacts\installer\TunnlSplitVPN-Setup-win-x64.exe`, creates Start menu and optional
desktop shortcuts, and registers an uninstaller in Windows Settings. It does not
require administrator rights to install. The build script tests installation and
uninstallation in an isolated directory. The setup packages the .NET 8 application
files; Windows needs the x64 .NET 8 Desktop Runtime to run the app. WireSock is a
separate installation and is not bundled. User tunnel files and settings are kept
when the app is uninstalled.

## WireSock references

- [WireSock Secure Connect command-line interface](https://wiresock.net/documentation/wiresock-secure-connect/command-line-interface.html)
- [WireSock advanced configuration parameters](https://wiresock.net/documentation/wiresock-secure-connect/advanced-parameters.html)
- [WireGuard configuration format](https://wiresock.net/documentation/wireguard/config.html)

The installed client on the development machine reported WireSock VPN Client 1.4.7.
Other WireSock versions and editions may expose different options; the application
uses only the `run`, `-config`, and `-log-level info` arguments shown by that client
and the WireSock documentation.
