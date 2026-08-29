# Blockbuster operations

Blockbuster is a framework-dependent .NET 10 application. Install the matching
.NET 10 ASP.NET Core Runtime and ffprobe on the host. Keep the application on a
trusted LAN or behind a VPN; the four-digit PIN features are not public-internet
security controls.

## Publish

From the repository root:

```powershell
dotnet restore
dotnet publish .\Blockbuster\Blockbuster.csproj -c Release --self-contained false -o .\artifacts\publish
```

Copy the publish directory to the target machine. Preserve the configured data
root separately from the application directory so upgrades do not overwrite the
database, keys, logs, artwork, or backups.

## Windows development and service hosting

Development remains a normal console process:

```powershell
dotnet run --project .\Blockbuster
```

For a service deployment, publish to a stable location such as
`C:\Services\Blockbuster`, create a dedicated local service account, and grant it:

- read and execute permission on `C:\Services\Blockbuster` and ffprobe;
- modify permission on the configured data root;
- read permission on every configured media root.

Register the framework-dependent application from an elevated terminal:

```powershell
sc.exe create Blockbuster start= auto binPath= '"C:\Program Files\dotnet\dotnet.exe" "C:\Services\Blockbuster\Blockbuster.dll"'
sc.exe description Blockbuster "Private Blockbuster media server"
sc.exe failure Blockbuster reset= 86400 actions= restart/5000
```

Configure the service account with `sc.exe config Blockbuster obj= ...` according
to local policy. Supply settings and secrets through service-scoped environment
configuration or an access-controlled deployment-local JSON file. At minimum,
production needs `Storage__DataRoot`; movie features later need
`MediaProbe__ExecutablePath`, `Tmdb__Token`, and the initial
`Authentication__BootstrapPin`. Start and inspect it with:

```powershell
sc.exe start Blockbuster
sc.exe query Blockbuster
```

## Linux systemd hosting

Create a locked-down service identity and directories using the distribution's
account-management tools. Install the published files at `/opt/blockbuster`, use
`/var/lib/blockbuster` as the data root, and grant the `blockbuster` user write
access only to the latter. Grant it read access to configured media mounts.

Store configuration in `/etc/blockbuster/blockbuster.env`, owned by root and mode
`0600`:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5080
Storage__DataRoot=/var/lib/blockbuster
MediaProbe__ExecutablePath=/usr/bin/ffprobe
Tmdb__Token=deployment-secret
Authentication__BootstrapPin=0123
```

Create `/etc/systemd/system/blockbuster.service`:

```ini
[Unit]
Description=Blockbuster private media server
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
User=blockbuster
Group=blockbuster
WorkingDirectory=/opt/blockbuster
ExecStart=/usr/bin/dotnet /opt/blockbuster/Blockbuster.dll
EnvironmentFile=/etc/blockbuster/blockbuster.env
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/lib/blockbuster

[Install]
WantedBy=multi-user.target
```

Then run `systemctl daemon-reload`, `systemctl enable --now blockbuster`, and
inspect logs with `journalctl -u blockbuster`. If media lives beneath a protected
home directory, mount it at a service-readable path instead of weakening
`ProtectHome`.

## HTTPS reverse proxy

Kestrel can remain bound to loopback while Caddy or Nginx terminates HTTPS. Enable
forwarded headers only with explicitly trusted proxy addresses:

```text
ReverseProxy__Enabled=true
ReverseProxy__ForwardLimit=1
ReverseProxy__KnownProxies__0=127.0.0.1
```

For Caddy, a minimal site is:

```caddyfile
blockbuster.example.test {
    reverse_proxy 127.0.0.1:5080
}
```

For Nginx, proxy to `http://127.0.0.1:5080` and set `Host`,
`X-Forwarded-For`, and `X-Forwarded-Proto`. WebSocket proxying must remain enabled
for Blazor Interactive Server. Use the proxy's actual source address in
`KnownProxies`; never trust arbitrary forwarded headers.

## Health and local operator commands

- `/health/live` reports process liveness.
- `/health/ready` checks SQLite, writable state directories, ffprobe, media roots,
  and TMDB configuration.

Stop the service before running local commands when practical, and run them as
the same service identity with the same configuration. Create a consistent,
timestamped SQLite backup with SQLite's online backup API:

```powershell
dotnet Blockbuster.dll operator backup
dotnet Blockbuster.dll operator backup --output D:\SafeBackups\blockbuster.db
```

The default destination is the configured backups directory. Existing output
files are never overwritten. Blockbuster does not schedule backups internally.

The local, non-echoed command entry point for administrator recovery is:

```powershell
dotnet Blockbuster.dll operator admin-pin reset
```

Its interactive prompt refuses redirected input so a PIN cannot be supplied on
the command line. The command becomes operational when milestone 07 introduces
the administrator credential store; until then it exits without prompting.
