# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-09-28

PONI 2 is a complete rewrite in **C# / WPF on .NET Framework 4.8**. It is still one portable
`PONI.exe` with nothing to install (.NET Framework 4.8 and WPF ship with Windows 10 / 11),
with a new interface, faster and more reliable network changes, and no more PS2EXE antivirus
false positives. Profiles from PONI 1.0 are imported automatically.

### Added

- **New interface**: sidebar (collapsible to icons), light / dark / system theme, in-window
  dialogs, status notifications, Windows 11 snap layouts, keyboard shortcuts (`Ctrl`+`1`/`2`/`3`,
  `Ctrl`+`N`, `Ctrl`+`F`, `F5`, `Ctrl`+`B`, `Enter` on a focused entry).
- **"This machine" cards**: every adapter live (state, DHCP / manual, IP, gateway, DNS,
  network type, Internet and address-conflict badges), virtual adapters on demand, and quick
  actions: configure, back to DHCP, private / public network.
- **"Applied" badge** on the profile an adapter is running right now (green when connected,
  grey when unplugged), and profile **search**.
- **Sorting** of the profiles list: name A→Z / Z→A, creation date, or a custom order
  rearranged by drag and drop (or "Move up / down"); the custom order is also the export order.
- **Automatic undo**: the adapter's configuration is saved before applying and restored if
  applying fails; an address already used on the network is detected; IP, gateway and DNS are
  verified afterwards (can be turned off in Settings).
- **Hyper-V**:
  - where the RJ45 port points is read live from Hyper-V;
  - the physical adapter used as RJ45 port can be chosen (no longer "Ethernet" only);
  - shared-port and orphan-switch anomalies are detected;
  - VMs that Hyper-V refuses to start because an adapter points to a deleted switch are
    detected, with a "Repair" button;
  - "Let VMs answer ping" (on by default): applying a profile inside a VM also creates
    PONI's own guest firewall rule `PONI-Ping-In` (ICMPv4 echo, local subnet only);
  - the whole module can be switched off in Settings, and is hidden when Hyper-V is absent.
- **Log viewer** inside PONI: one day at a time, everything or problems only, exception
  details, copy for a bug report. Daily log files, kept 30 days.
- **Single instance**: launching PONI again brings the open window to the front.
- Unit tests (xUnit) and a GitHub Actions workflow that tests and builds every push.

### Changed

- **English by default** on first launch (French one click away in Settings).
- **Stricter validation**, shared by the form, import and migration: strict IPv4, mask as
  `255.255.255.0`, `24` or `/24`, reserved / network / broadcast addresses refused, every DNS
  entry checked, gateway outside the subnet flagged.
- **New data file** `%APPDATA%\PONI\store.json`, written atomically with an automatic backup;
  an unreadable file is set aside and restored from the backup instead of blocking PONI. The
  v1 `profiles.json` is migrated on first launch and left untouched.
- **Import / export**: only the portable fields are exported (nothing about this PC); import
  skips invalid entries one by one instead of failing entirely.
- **"Set the network to Private"** is now a setting, off by default (v1 did it silently).
- The **VM user name** is remembered only after a successful sign-in; the password never is.
- Giving the RJ45 port to a VM **unplugs every other VM** from it (one destination at a time).
- **Faster**: window in about 0.5 s, Hyper-V state read in about 0.6 s instead of 7-10 s.

### Fixed

- Applying a profile to the host adapter that serves a VM as RJ45 port failed with "element
  not found": PONI now offers to give the port back to Windows first.
- Giving the RJ45 port back to Windows left the VMs' adapters pointing to the deleted switch,
  and Hyper-V then refused to start them ("insufficient system resources").
- Giving a static address to an unplugged adapter that was on DHCP failed ("Inconsistent
  parameters PolicyStore PersistentStore and Dhcp Enabled").
- Editing a profile lost its "last applied" information.
- One invalid entry made a whole import fail.
- The diagnostic log grew forever.

### Removed

- The PowerShell / PS2EXE v1 sources. PONI 1.0 remains available from its
  [release](https://github.com/DoodzProg/PONI/releases/tag/v1.0.0).
- The resizable activity log at the bottom of the window (replaced by notifications and the
  log viewer).
## [1.0.0] - 2026-09-03

First public release.

### Added

- **Network profiles** screen: create, edit, delete and apply IP profiles
  (address, subnet mask, gateway, multiple DNS) with live field validation.
- Apply a profile to a **host network adapter** or **inside a running Hyper-V VM**
  (PowerShell Direct); the in-VM adapter is matched by MAC address.
- **"Import current config"**: pre-fill a new profile from an adapter's live settings.
- **Editable "current network configuration" table**: one column per adapter, with
  per-adapter dropdowns for DHCP / manual assignment and Private / Public network type,
  and click-to-edit IP / mask / gateway / DNS applied atomically.
- **One-click "back to DHCP"** repair action per adapter.
- **Export / import** profiles as JSON, all of them or a hand-picked selection.
- **Simple / detailed** profile table view, with a per-profile "last applied" timestamp.
- **RJ45 port for VM** screen: route the physical RJ45 port to the host or to a VM;
  PONI creates and removes the required Hyper-V switch and the in-VM adapter automatically.
- Full **French / English** interface, switchable at any time.
- Resizable, timestamped, colour-coded **activity log** with a shortcut to the log folder.
- Asynchronous execution (the UI never freezes) and real-state verification after every
  network operation.
- Portable single `.exe` (PS2EXE), no installer, self-elevating (UAC). Data stored in
  `%APPDATA%\PONI\`. Automatic one-time migration from a legacy `%APPDATA%\NetManager\`.

[2.0.0]: https://github.com/DoodzProg/PONI/releases/tag/v2.0.0
[1.0.0]: https://github.com/DoodzProg/PONI/releases/tag/v1.0.0
