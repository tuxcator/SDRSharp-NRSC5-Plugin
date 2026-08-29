# Installation on Windows

This guide installs the **SDRSharp NRSC-5 HD Radio** plugin into a standalone copy of SDR# x64.

*[Versión en español](INSTALACION.md)*

## 1. Requirements

- 64-bit Windows 10 or 11.
- SDR# x64 with .NET 9 plugin support (`SDRSharp.dotnet9.exe`).
- An Airspy HF+ Discovery or RTL-SDR connected over USB.
- The matching driver for your receiver. RTL-SDR normally needs WinUSB through Zadig.
- An FM station that actually broadcasts HD Radio/NRSC-5.
- An IQ sample rate of at least **744.1875 kS/s**, which is what the decoder requires.

> The package is x64 only. It does not work inside `sdrsharp-x86`; the installer reads the
> executable's PE header and stops if it finds a 32-bit build.

## 2. Windows 11 Smart App Control

The plugin and `libnrsc5` are community builds without a commercial signature. When **Smart App Control** is on, Windows can block `SDRSharp.NRSC5.dll` and SDR# will not show the NRSC-5 entry.

The block shows up in `PluginError.log` as `0x800711C7`, and in Code Integrity as event 3077.

Supported options:

1. Use binaries signed with a certificate from an authority Microsoft recognises.
2. Turn Smart App Control off manually under **Windows Security > App & browser control > Smart App Control settings**.

Warning: turning Smart App Control off lowers the protection of the machine. Microsoft states it cannot be turned back on without resetting or reinstalling Windows. There is no per-file exception, and a locally self-signed certificate does not satisfy this policy.

Restart Windows after changing the setting.

## 3. Download

1. Open the repository's **Releases** section.
2. Download `SDRSharp-NRSC5-Plugin-v0.1.0-win-x64.zip`.
3. Extract the ZIP completely. Do not run the installer from inside the compressed file.

> The number in the ZIP is the assembly version. The panel header also shows the development
> build in progress, for example `DEV 3.2`.

The package contains:

```text
SDRSharp-NRSC5-Plugin\
├── NRSC5Runtime\           six native DLLs: libnrsc5 and its dependencies
├── SDRSharp.NRSC5.dll      the plugin
├── Install-Package.ps1     installer
├── Instalar.cmd            drag and drop shortcut
├── Plugin.xml.fragment     entry for SDR# versions that use Plugins.xml
├── LICENSE
├── README.md
└── THIRD_PARTY_NOTICES.md
```

## 4. Automatic install

Close SDR# and run from PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install-Package.ps1 -SdrSharpDir "C:\Path\To\sdrsharp-x64"
```

You can also drag your SDR# x64 folder onto `Instalar.cmd`.

The installer creates this layout:

```text
sdrsharp-x64\
├── NRSC5Runtime\
│   ├── libnrsc5.dll
│   ├── libfftw3f-3.dll
│   ├── libgcc_s_seh-1.dll
│   ├── librtlsdr.dll
│   ├── libusb-1.0.dll
│   └── libwinpthread-1.dll
└── Plugins\
    └── SDRSharp-NRSC5-Plugin\
        └── SDRSharp.NRSC5.dll
```

The native DLLs must stay outside `Plugins`. SDR# scans that folder recursively and may try to open them as managed assemblies. If you are upgrading from an older version that kept them inside, the installer deletes that leftover copy.

## 5. Verify the plugin

1. Run `SDRSharp.dotnet9.exe`.
2. Look for **NRSC-5 HD Radio by tuxcator** under the **Digital Radio** panel.
3. If the entry is missing, close SDR# and check `PluginError.log` in the main SDR# folder.

## 6. Set up the receiver

### Airspy HF+ Discovery

- Source: **AIRSPY HF+ Series**.
- Sample rate: `768 ksps` (`912 ksps` works as well).
- Mode: `WFM`.
- Tune the exact station centre, for example `103.700 MHz`.
- Use roughly `400 kHz` of bandwidth so the NRSC-5 digital sidebands are covered.
- **Gain:** turn **AGC** and **Preamp** on in the *Source* panel, and leave **ATT** at 0 dB. With
  the gain low, analog FM still sounds fine and even decodes RDS, but the digital sidebands sit
  buried in the noise and MER collapses. This is the most common cause of "it tunes but never
  locks onto HD".

### RTL-SDR

- Source: RTL-SDR USB.
- Driver: WinUSB.
- Sample rate: `1.024 MS/s` or `1.2 MS/s`.
- Mode: `WFM`.
- Avoid excessive gain that would overload the receiver.

## 7. Decoding HD Radio

1. Tune the exact centre of the FM station.
2. Open the NRSC-5 panel.
3. Turn on **Enable HD decoding**.
4. Leave **Auto HD audio** on so digital audio replaces the analog one on lock, and falls back to analog when the signal is lost.
5. Use **PREVIOUS** and **NEXT** to move between the subchannels the station broadcasts.

Locking depends on receiving both digital sidebands cleanly. A strong analog signal is no guarantee of enough MER for HD Radio.

### Subchannel selection

- Changing station **always starts on HD1**. The subchannel line-up belongs to the station, not to
  the listener, so carrying an HD2 or HD3 choice over from the previous station would leave the
  decoder waiting for audio that never arrives while the analog programme plays.
- Fine tuning the same station (less than 50 kHz) keeps the subchannel you are listening to.
- **PREVIOUS** and **NEXT** step only through the subchannels announced by the SIG table and the
  audio service descriptors. Until that information arrives they cycle HD1 to HD8 so you are never
  trapped on HD1.
- Beyond that the selector never moves on its own: it changes only when you press it.

### Subchannel changes without analog

Moving from one subchannel to another does not let the analog programme through: the level is ramped
down over 20 ms, silence covers the buffer refill, and the new subchannel ramps back in. The silent
gap lasts as long as your **Buffer** setting; lower it if you want a shorter one. The hold expires
after 3 seconds or the buffer plus 2 seconds, whichever is longer, so a subchannel that never
delivers audio cannot leave you in permanent silence.

### HD audio buffer

The **Buffer** checkbox enables a prebuffer adjustable between 0.1 and 10 seconds. More seconds ride
out brief fades better; fewer seconds reduce the latency against the analog audio. With the box off,
HD audio starts on the first decoded block. The status line shows the current fill against the
target and turns coloured once the threshold is reached.

The tolerance for a sync loss is never shorter than the configured buffer, so raising the buffer also
makes the automatic return to analog audio more patient.

### Surround effect

The **Surround** button widens the HD stereo image. It separates mid and side, boosts the side
signal and adds a copy delayed by 14 ms, which is what the ear reads as space. Bass below 250 Hz
stays centred so the mix is not hollowed out and does not cancel on a mono speaker, and a soft
limiter holds the peaks.

It only touches the HDC codec audio: the analog audio SDR# produces is left alone. The centre level
does not change when you enable it, so it reads as width rather than as volume. The state is not
persisted: every SDR# session starts at `Surround OFF`.

## Professional monitor and artwork

The panel reports separately:

- RF power measured in dBFS over the received IQ.
- Estimated dBm level. Trim the **dBm** field against a known reference signal; this value is no substitute for a calibrated RF meter.
- SNR derived from the average MER of both sidebands.
- Lower/upper MER and BER.
- Real bitrate of the HDC packets of the selected subchannel.
- Continuous IQ rate, VFO offset and dBFS peak, with no extra FFT load.
- HD buffer state and the list of subchannels the station actually broadcasts.
- Artwork sent by the station through ID3/XHDR and LOT, centred in a square frame and corrected for its EXIF orientation. When the station sends no song art its logo is shown, and when it sends no image at all the HD Artwork placeholder appears.

## 8. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| The NRSC-5 panel is missing | Smart App Control blocked the DLL | Check `PluginError.log`, event 3077 and section 2 |
| `0x800711C7` error | Code Integrity policy | Use signed binaries or change Smart App Control deliberately |
| `libnghttp3-9.dll` is not designed for Windows | Old package with foreign dependencies | Remove the old install and install the current version |
| Architecture error while installing | SDR# x86 with an x64 runtime | Install SDR# x64 in a separate folder |
| Analog FM and even RDS decode fine, but MER is negative and HD never locks | Receiver gain too low | Turn **AGC** and **Preamp** on and set **ATT** to 0 dB in the *Source* panel |
| `IQ sample rate too low` in the panel | Sample rate below 744.2 kS/s | Raise it to 768 ksps on the HF+ or 1.024 MS/s on RTL-SDR |
| No HD lock | Frequency, bandwidth, sample rate or MER insufficient | Centre the frequency, use 400 kHz and check antenna and gain |
| Analog plays but HD never does | The station does not broadcast NRSC-5, or its sidebands are weak | Try a confirmed HD station and watch MER/BER |
| Gaps when changing subchannel | Large buffer | The silence lasts as long as **Buffer**; lower it to shorten it |

## 9. Uninstall

Close SDR# and delete only:

```text
C:\Path\To\sdrsharp-x64\Plugins\SDRSharp-NRSC5-Plugin
C:\Path\To\sdrsharp-x64\NRSC5Runtime
```

Do not delete other plugin folders or SDR#'s own files.

## 10. Build from source

Clone the repository and run:

```powershell
.\Compilar.cmd
```

The script downloads the official SDR# plugin SDK, prepares a local .NET 9 SDK, imports the NRSC5 Win64 runtime, builds, runs the tests and produces the ZIP inside `dist`.

By default it looks for the nrsc5 Win64 runtime in a sibling folder, `..\FM-DX-Windows-Portable\runtime\nrsc5`. To point it somewhere else:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Nrsc5Runtime "C:\Path\To\nrsc5"
```

To install what you just built:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Install.ps1 -SdrSharpDir "C:\Path\To\sdrsharp-x64"
```
