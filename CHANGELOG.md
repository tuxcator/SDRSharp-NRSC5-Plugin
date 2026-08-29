# Changelog

*[Versión en español](CHANGELOG.es.md)*

## Unreleased — development build 3.2

- Every station change now starts on HD1. The subchannel line-up belongs to the station, so
  carrying an HD2 or HD3 choice over to a station that only broadcasts HD1 left the decoder
  waiting for audio that never arrived while the analog programme played. Fine tuning the same
  station, under 50 kHz, keeps the current subchannel, and the selector never moves on its own
  in any other case.
- Changing subchannel no longer lets the analog programme through. The level ramps down over
  20 ms, silence covers the buffer refill, and the new subchannel ramps back in. The hold
  expires after 3 seconds or the buffer plus 2 seconds, whichever is longer, so a subchannel
  that never delivers audio cannot leave the listener in permanent silence.
- New **Surround** button that widens the HD stereo image: mid and side are separated, the side
  signal is boosted and mixed with a copy delayed by 14 ms, and bass below 250 Hz stays centred
  so the mix is not hollowed out and does not cancel on a mono speaker. It only touches the HDC
  codec audio, never SDR#'s analog path, and the centre level is left untouched so the effect
  reads as width rather than as volume.
- The panel header shows the development build in progress.
- Documentation is published in English and Spanish: the English README is the repository front
  page, `docs/INSTALLATION.md` mirrors `docs/INSTALACION.md`, and both guides now use the real
  control names, state the 744.1875 kS/s requirement and document the Airspy HF+ gain settings.

## Unreleased

- The station logo is now shown in the artwork frame. It arrived over LOT but no ID3 XHDR ever
  references it, so it was cached and never painted.
- Song artwork is resolved by priority: the image the current XHDR points at, then the most
  recent art seen on that subchannel, and finally the station logo. Stations that send art
  without a matching XHDR no longer leave the frame empty.
- The image cache is keyed by port and LOT id. A LOT id is only unique within its service, so
  images from two subchannels could previously overwrite each other.
- The `nrsc5_event_t` offsets are derived in `Nrsc5Layout` from the C alignment rules instead of
  being written by hand at every read, and the smoke test compares them against the values in
  the official x64 header.
- Resampling with a polyphase anti-alias filter of Kaiser-windowed sinc, its length scaling with
  the decimation ratio. Out-of-band rejection goes from -12 dB to -90 dB at 400 kHz with an
  RTL-SDR at 2.4 MS/s; the previous linear interpolation folded the adjacent channel onto the
  digital sidebands.
- The VFO mixing now happens at the input rate, before decimation, which is the only correct
  order when the anti-alias filter is centred on DC.
- Optional HD audio buffer, adjustable between 0.1 and 10 seconds, with a fill indicator. With it
  off, HD audio starts on the first decoded block for minimum latency.
- The tolerance for a sync loss is never shorter than the configured buffer.
- Previous/Next steps only through the subchannels the station actually broadcasts, discovered
  from the SIG table and the audio service events. The panel lists the available ones.
- `_hdAudioActive` is always read and written inside `_audioGate`; the SDR# audio thread and the
  UI thread used to touch it without synchronisation.
- Fonts are shared from `PanelFonts` instead of being constructed per control, which leaked a GDI
  handle per label every time the panel was rebuilt.
- The regression guards in `tests\Test-Project.ps1` that had lost their backslashes, and could
  therefore never fail, were fixed and verified against the previous code.

- Song artwork received over ID3/XHDR and LOT, centred in the monitor.
- Artwork centred in a 1:1 frame, with zoom fitting and EXIF orientation correction.
- Professional dark interface with larger, legible technical cards.
- Separate metrics for dBFS power, calibrated dBm estimate, SNR/MER, per-sideband MER, BER and
  HDC bitrate.
- FFT analyser removed to cut load and prioritise the technical signal information.
- Plugin interface fully translated to English.
- The HD1-HD8 selector replaced by Previous/Next buttons and moved above Signal Analysis.
- Inner scrolling removed; the monitor lays out square artwork and metrics through proportional
  rows as it is resized.
- Initial HD prebuffer of roughly 743 ms to absorb jitter and avoid frequent switching between HD
  and analog audio.
- 1.5 seconds of tolerance for brief sync losses before the HD audio is flushed.
- Center Frequency changes now update only the digital mixer and no longer restart the decoder.
- A brief underflow keeps the HD path armed and no longer demands refilling the whole prebuffer.
- A sustained loss is confirmed from the last valid digital PCM block, not only from the initial
  sync event.
- Reassigning the same IQ rate no longer resets the resampler's running state.

## 0.1.0

- First standalone release of the SDRSharp NRSC-5 plugin for Windows x64.
- Captures IQ from SDR# without opening the receiver a second time.
- Initial support for the Airspy HF+ Discovery and RTL-SDR.
- Selection of HD1 to HD8 services.
- HD audio with automatic fallback to analog FM.
- Sync, MER, BER, station, title and artist data.
- Native runtime reduced to the six DLLs actually needed.
- Runtime installed outside `Plugins` so SDR# does not scan native DLLs.
- Documented diagnostics for Smart App Control and the `0x800711C7` error.
