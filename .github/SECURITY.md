# Security

If you find a security issue in Trueforce For All, please report it
privately rather than opening a public issue. Email **mhytee@gmail.com**
with a description and reproduction steps. I'll acknowledge within a
reasonable window and coordinate a fix and disclosure.

## Scope

This plugin runs in user space inside SimHub but interacts with USB
traffic via [USBPcap](https://github.com/desowin/usbpcap), a kernel-mode
filter driver bundled with our installer. The relevant surface includes:

- The USBPcap parser in `src/TrueforceForAll.Core/UsbPcapFfbTap.cs`,
  which consumes pcap-formatted bytes streamed from `USBPcapCMD.exe`.
- The HID write path in `src/TrueforceForAll.Core/TrueforceDevice.cs`,
  which sends frames to the wheel.
- The audio loopback helper in
  `src/TrueforceForAll.LoopbackHelper/`, a child process that captures
  game audio via WASAPI loopback.

Issues in USBPcap itself should go to the upstream project.

## Out of scope

Bugs that crash the plugin, drop audio, or misbehave on the wheel are
not security issues. Open a regular GitHub issue for those.
