# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

_Nothing yet._

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

[Unreleased]: https://github.com/DoodzProg/PONI/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/DoodzProg/PONI/releases/tag/v1.0.0
