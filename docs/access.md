# Device access

Status: built in 1.9.0. The contributor's PR #15 was used as reference only; this code was written fresh.

Right-click a device and choose **Access** to see every way into it, from what the scan recorded. Opening the menu sends nothing. Clicking an endpoint opens it with the existing browser, SSH or Remote Desktop action on that port. An endpoint no app opens (plain TCP) copies its address instead. **Copy Address** at the bottom copies any endpoint's address.

## Decisions

| Topic | Decision |
| --- | --- |
| Placement | An Access submenu on the device menu, beside the SSH and Remote Desktop items. No new window. Each entry's tooltip explains its evidence and when it was seen. |
| Web probes | Normal scans are unchanged: they stop at the first web port that gives a title or Server header. Deep scans check every web port, so each one has its own evidence. |
| Open in Browser | Opens the web endpoint that answered, HTTPS first, and falls back to `http://address/` only when none did. The Services view opens each port with the scheme it answered with. |

## What an entry says

The wording never claims more than was seen.

- **answered 200** (or any status): a web server answered at the root address. Whether it needs a login is not known. A 200, 403 or redirect does not mean no login is needed.
- **asks for a login (Basic)**: the server answered 401. Only the scheme from `WWW-Authenticate` is kept; realms and other values are not.
- **sign-in page**: the page has a form with a password field. This says a login exists, not that one would work. Comments, scripts and styles are ignored. Logins built by JavaScript or single sign-on are missed.
- **banner received**: the SSH server sent its banner. No login was attempted.
- **port open**: the port accepted a connection. The protocol is guessed from the port number.

KillerScan never sends credentials, never logs in, and never stores passwords.

## Addresses

Addresses are built only from the device's numeric IP and the endpoint's protocol and port, never from anything the device sent, such as a redirect target. Only `http`, `https`, `ssh`, `rdp` and `tcp` with ports 1 to 65535 are allowed, and devices without a numeric address have no endpoints.

A redirect that stays on the same address (up to two hops, as the fingerprint already allowed) is recorded twice: the first port records the redirect status, and the page it led to is recorded under the port and scheme that served it. A redirect anywhere else, or to a raw print port (9100 to 9103 or 515), is recorded as the redirect status only and not followed. A redirect is recorded even when the page it points to does not answer.

## What is recorded

`NetworkDevice.Access` lists the web endpoints that answered: `Protocol`, `Port`, `Evidence` (`HttpResponse`), `HttpStatus`, `Login` (`Unknown`, `Requested` or `LoginForm`), `AuthScheme` and `ObservedAt` in UTC. It is null when no web response was recorded.

`DeviceAccess.ForDevice(device)` adds every other open port: port 22 with a banner as `SshBanner`, the rest as `PortOpen` with no time. `DeviceAccess.Address` builds the address and `DeviceAccess.BestWeb` picks the default web endpoint. None of them sends anything.

## Where it appears

- The **Access** submenu on the device menu.
- **Open in Browser** (Enter) and the Services view's browser action.
- **HTML report**: hovering a device's open ports lists its endpoints.
- **CSV**: an `Access` column after Confidence and Evidence, in English, such as `https:8443 200 login-form; ssh:22 banner`.
- **CLI JSON** and the PowerShell module: the `Access` list on each device.

## Not in this release

- Scan history does not store access evidence.
- The Services view has no Access submenu; its browser action uses the recorded scheme.
- HTTPS fingerprinting does not validate certificates, so an answer on HTTPS says nothing about the server's identity. The browser applies its own certificate checks when opened.
- Only the root address `/` is requested. No paths are guessed.
- Checked only against loopback fixtures, not against real devices on a site.
