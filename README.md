# SDRSharp NRSC-5 HD Radio Plugin

An independent, experimental plugin that decodes FM HD Radio/NRSC-5 broadcasts inside SDR#, without
opening the Airspy HF+ or RTL-SDR a second time. It taps the IQ stream SDR# is already receiving,
so the receiver stays under SDR#'s control while the plugin decodes the digital sidebands alongside
the analog audio.

*[Versión en español](README.es.md)*

[![Donate with PayPal](https://img.shields.io/badge/PayPal-donate-00457C?logo=paypal&logoColor=white)](https://paypal.me/EmmanuelM183)

## See it running

![Stepping through HD1, HD2 and HD3 on 103.7 MHz](docs/media/demo-hd1-hd2-hd3.gif)

Stepping through the three subchannels of one station on 103.7 MHz: the artwork, the metadata and
the whole signal analysis follow the selected subchannel, and the audio never drops back to analog
in between. [Watch the full 50-second capture with audio](docs/media/demo-hd1-hd2-hd3.mp4).

### Screenshots

The same station on each of its three subchannels. Click any image for the full resolution.

| HD1 · La Ke Buena | HD2 · LA AW | HD3 · Milenio Radio |
|---|---|---|
| [![HD1](docs/media/screenshot-hd1.png)](docs/media/screenshot-hd1.png) | [![HD2](docs/media/screenshot-hd2.png)](docs/media/screenshot-hd2.png) | [![HD3](docs/media/screenshot-hd3.png)](docs/media/screenshot-hd3.png) |

Captured with an Airspy HF+ Discovery at 912 ksps. The `Device SN` field is masked on purpose.

## Documentation

- [Full installation guide](docs/INSTALLATION.md)
- [Guía de instalación en español](docs/INSTALACION.md)
- [Changelog](CHANGELOG.md)
- Ready-to-install packages are published under [Releases](https://github.com/tuxcator/SDRSharp-NRSC5-Plugin/releases).

## What it does

- Captures raw IQ through the official SDR# plugin API.
- Digitally centres the selected VFO and resamples to 744187.5 complex samples per second.
- Feeds `libnrsc5` through `nrsc5_open_pipe` and `nrsc5_pipe_samples_cf32`.
- Reports lock state, MER, BER, station name, title, artist and album.
- Receives and displays song artwork and the station logo from ID3/XHDR events and LOT files, with a
  priority fallback for stations that never send an XHDR.
- Replaces the analog audio with HD PCM while locked, and returns to analog when the signal is lost.
- Optional HD audio buffer, adjustable in seconds, with a fill indicator.
- Professional monitor: dBFS power, calibrated dBm estimate, SNR/MER, per-sideband MER, BER and the
  real HDC bitrate of the selected subchannel.
- Subchannels HD1 to HD8, with a Previous/Next selector that steps only through the ones the station
  actually broadcasts, discovered from the SIG table.
- Optional surround effect that widens the HD stereo image.
- Responsive layout with no inner scrolling: the square artwork and the metric cards scale with the
  panel.

## Requirements

- 64-bit Windows 10 or 11.
- SDR# x64 with .NET 9 plugin support (`SDRSharp.dotnet9.exe`). The package is x64 only.
- Airspy HF+ Discovery at `768 ksps` (or `912 ksps`), or an RTL-SDR at `1.024 MS/s` or more. The
  decoder needs at least **744.1875 kS/s** of IQ.
- An FM station that actually broadcasts NRSC-5.

## Install

Close SDR#, then run:

```bat
Instalar.cmd "C:\Path\To\SDRSharp"
```

You can also drag your SDR# folder onto `Instalar.cmd`. The installer places:

- `SDRSharp.NRSC5.dll` in `Plugins\SDRSharp-NRSC5-Plugin`.
- The six native DLLs in `NRSC5Runtime`, next to the SDR# executables and outside `Plugins`, because
  SDR# scans that folder recursively and would try to load them as managed assemblies.
- The `Plugin.xml` entry when the SDR# build uses that file. Newer builds detect the assembly from
  its plugin directory on their own.

The [installation guide](docs/INSTALLATION.md) covers the whole process, including Windows 11 Smart
App Control and a troubleshooting table.

## Recommended setup

1. Select the Airspy HF+ or RTL-SDR source in SDR#.
2. Use `WFM` and tune the exact centre of the station, for example 103.7 MHz.
3. Give the RF bandwidth room for the whole hybrid signal, roughly 400 kHz.
4. Airspy HF+: `768 ksps`. RTL-SDR: `1.024` or `1.2 MS/s`.
5. On the Airspy HF+, turn **AGC** and **Preamp** on and leave **ATT** at 0 dB. With the gain low,
   analog FM sounds fine and RDS still decodes while the digital sidebands stay buried in the noise —
   the most common reason HD never locks.
6. Open **Digital Radio > NRSC-5 HD Radio by tuxcator**.
7. Turn on **Enable HD decoding**, and leave **Auto HD audio** on so the plugin switches between
   analog and HD by itself.

## Using the panel

**Subchannels.** Every station change starts on HD1: the subchannel line-up belongs to the station,
so carrying an HD2 or HD3 choice over to a station that only broadcasts HD1 would leave the decoder
waiting for audio that never arrives. Fine tuning the same station, under 50 kHz, keeps the
subchannel you are on. Beyond that the selector only moves when you press **PREVIOUS** or **NEXT**.

**Switching subchannels** does not let the analog programme through: the level ramps down over 20 ms,
silence covers the buffer refill, and the new subchannel ramps back in.

**Buffer.** A prebuffer adjustable between 0.1 and 10 seconds. More seconds ride out brief fades;
fewer reduce latency against the analog audio. It also sets how patiently the plugin waits before
falling back to analog, and how long the silent gap is when you change subchannel.

**Surround.** Widens the HD stereo image: mid and side are separated, the side signal is boosted and
mixed with a copy delayed by 14 ms, and bass below 250 Hz stays centred so the mix does not hollow
out or cancel on a mono speaker. It only touches the codec audio, never SDR#'s analog path, and the
centre level is left alone so it reads as width rather than as volume.

## Build from source

```bat
Compilar.cmd
```

The script downloads the official SDR# plugin SDK and a local .NET 9 SDK, builds, runs the tests and
produces the package in `dist\SDRSharp-NRSC5-Plugin`. By default it looks for the nrsc5 Win64 runtime
in a sibling folder:

```text
..\FM-DX-Windows-Portable\runtime\nrsc5
```

To point it somewhere else:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Nrsc5Runtime "C:\Path\To\nrsc5"
```

## Windows 11 Smart App Control

The plugin and the native runtime are built locally and carry no commercial signature. With Smart App
Control **On**, Windows can block `SDRSharp.NRSC5.dll` and log Code Integrity event 3077. There is no
per-file exception: either use a build signed by an authority Microsoft recognises, or decide in
Windows Security whether to turn that protection off. Turning it off lowers security, and Microsoft
states it cannot be turned back on without resetting or reinstalling Windows.

## Known limitations

- The bundled native runtime is Win64. SDR# x86 would need nrsc5 and its dependencies rebuilt for 32
  bits.
- Reception depends on the station broadcasting NRSC-5 and on both digital sidebands having enough
  MER. A strong analog signal is no guarantee.
- The surround setting is not persisted: every SDR# session starts with it off.
- The plugin does not transmit, encrypt, or bypass access controls. It only decodes broadcasts that
  are received legally.

## Licensing

The plugin code is distributed under GPL-3.0-or-later for compatibility with nrsc5. The SDR# SDK
assemblies are downloaded from Airspy and must not be republished inside this repository.

## Support

This plugin is free software and is developed in my spare time. If it is useful to you, a donation
is a welcome way to say thanks — it is entirely optional and grants no privileges, priority support,
or influence over the roadmap.

[![Donate with PayPal](https://img.shields.io/badge/PayPal-donate-00457C?logo=paypal&logoColor=white)](https://paypal.me/EmmanuelM183)

<https://paypal.me/EmmanuelM183>
