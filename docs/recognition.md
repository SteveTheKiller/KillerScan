# Recognition evidence

Status: built in 1.9.0, following review backlog item B07. The contributor's PR #9 was used as reference only; this code was written fresh.

Every device type in KillerScan now says why. Hover over the type for a short summary, or right-click a device and choose **Why This Type?** (Ctrl+I) for the full list. Opening it reads what the scan already recorded and sends nothing to the device.

## Decisions

| Topic | Decision |
| --- | --- |
| Weak guesses | Kept, marked Low. A device with no open ports and a MAC still shows IoT, a private MAC still shows Mobile, and so on. The evidence names the rule that produced the guess. |
| Placement | A tooltip on the type cell, plus the Why This Type? window from the device menu and Ctrl+I. The table layout is unchanged. |
| Hostname keywords | Heavy evidence (weight 20), not a short circuit. Clearer protocol answers can outvote a name, and the name's type then shows as an alternative. |
| Manual types | Win, are graded "Set manually", and keep the automatic result alongside. Clearing the manual type restores it. |
| Default gateway | Still decided by Windows' routing table before scoring, graded High. |

## Confidence

Confidence grades how well the evidence fits the type. It is not a probability, and every protocol string is self-reported, so even High does not prove what a device is.

- **High**: two or more different protocol families support the type, and no unrelated type scored close.
- **Medium**: one protocol family supports it, or the score reaches 15 from names, vendors and ports alone (for example the Active Directory port set, or a hostname keyword).
- **Low**: only weak hints, or a fallback guess because nothing reached the threshold of 6.
- **Set manually**: a technician chose the type.

Protocol families are web page (HTTP title and Server header), TLS certificate, SSH banner, SNMP, NetBIOS name, mDNS service and UPnP server. The name, MAC vendor, open ports and TTL are weak hints, because they are inferred from outside rather than answered by the device.

A **conflict** is an unrelated type scoring at least two thirds of the winner. It lowers the grade one step. Refinements of each other are not conflicts: Windows and Windows Server; Network, Router and Switch/AP; iPhone, Apple Device and Apple TV; Smart TV, Media Streamer, Apple TV and Apple Device.

Scoring is the existing rule set unchanged, apart from the hostname weight. Ties go to the rule listed first, so the result never depends on dictionary order.

## What is recorded

`NetworkDevice.Recognition` holds:

- `Type` and `Confidence`: what is shown.
- `AutomaticType` and `AutomaticConfidence`: what the evidence alone says, even under a manual type.
- `Signals`: the evidence for the type and its refinements (a network-vendor hint is listed for Router), each with a stable `Source` (`hostname`, `vendor`, `ports`, `ttl`, `http`, `tls`, `ssh`, `snmp`, `netbios`, `mdns`, `ssdp`, `gateway`, `fallback`), the `Detail` seen and its `Weight`. Fallback and gateway details are stable rule keys, translated for display.
- `Alternatives`: up to three other types that reached the threshold.
- `Conflict`.

Observed values are clipped to 80 characters and control characters become spaces. Only source names and rule sentences are translated; observed values are shown as the device sent them.

## Where it appears

- **Type cell tooltip** in the Devices table: confidence, the automatic result when a manual type is set, the three strongest signals, and the conflict note.
- **Why This Type?**: every signal, alternatives, the conflict note and the caveat.
- **HTML report**: the tooltip summary as the type cell's hover title, escaped.
- **CSV**: `Confidence` and `Evidence` columns after the existing six, in English (`mdns: _ipp._tcp; ports: 9100`). Scripts that read columns by position keep working.
- **CLI JSON** and the PowerShell module: the full `Recognition` object on each device.
- **Demo mode**: the real classifier's view of the invented devices. Where it disagrees with the preset type, the preset shows as set manually.

## Not in this release

- Scan history does not store evidence, so saved scans and their exports have none. History rows show the type only.
- The Services view has no tooltip; its rows are per port.
- No hardware model is reported or inferred.
- Observation times per signal, and marking cached mDNS or SSDP data as not fresh, are not recorded.
- The corpus behind the grades is a hand-labeled set of fabricated devices in `tests/KillerScan.Engine.Tests/RecognitionTests.cs`, including weak, empty and contradictory devices. It has not been measured against real site captures.
