# Changelog

*[Versión en español](CHANGELOG.es.md)*

## Unreleased — development build 3.3.3

- **A transmitter site that contradicts the call sign is now flagged rather than named.**
  Build 3.3.1 stated whatever town the coordinates geocoded to as fact. Tuning XHPQ-FM
  through a receiver in Querétaro showed what that costs: the station broadcasts
  coordinates in San Marcos, California, some 2500 km away, together with a US country
  code and an FCC facility ID of 22 that no FCC database lists. Its exciter was never
  configured. The panel confidently named a town the station demonstrably does not
  transmit from.
- The call sign now decides which country a station belongs to, overriding the country
  code in its SIS frames. Of everything in the identity block it is the one field a
  station gets right, because it is what listeners read; the rest is set once at
  installation and often left at whatever the exciter shipped with. `K` and `W` are the
  United States, `X` is Mexico, `C` is Canada.
- When the geocoded site lands in a different country from the call sign, the cell shows
  the raw coordinates with a question mark and the tooltip says what is wrong. The FCC is
  no longer queried at all for a station whose call sign is not American.

## Unreleased — development build 3.3.1

- **LOCATION now names the town the transmitter stands in**, not a pair of coordinates.
  The coordinates SIS carries are reverse geocoded through the US Census geocoder, which
  is public-domain government data and needs no key, falling back to OpenStreetMap's
  Nominatim outside the United States. Nominatim's policy is respected: an identifying
  User-Agent, at most one request per second, and results cached for 180 days.
- The transmitter town is regularly **not** the community of licence, so both are now
  shown: the transmitter town in the cell, and the community of licence beside it in the
  tooltip. KQRS is licensed to Golden Valley and transmits from Shoreview, fifteen
  kilometres away, and it is the second one that says where to point an antenna.
- Google Maps was considered and not used: its geocoding API requires a key and a billing
  account, which cannot ship inside a public plugin.
- The memory-and-disk cache behind the FCC query and the geocoder is now one shared piece
  of code, and the file it writes carries a format version. Without it, the cache written
  by build 3.3 was read back as "the service has no record" and would have blanked the
  licence fields for the thirty days of its lifetime after upgrading.

## Unreleased — development build 3.3

- New **station information** row under the song details, showing the station's **slogan**,
  its **PI code**, its **location** and its **power**.
- The slogan, the call sign, the FCC facility ID and the transmitter site now come from the
  SIS frames the station itself broadcasts. libnrsc5 was already delivering those events;
  the plugin was discarding all of them except the station name.
- The community of licence, the ERP and the HAAT come from the FCC's public FM Query service,
  looked up by the facility ID the station transmits. The answers are cached in memory and on
  disk for 30 days, since a listener sweeping the band revisits the same stations constantly
  and a licence changes at most a few times a year. The lookup runs off the decoder thread and
  failing costs only those three fields: everything from SIS is already on screen. Stations
  licensed outside the US are not queried at all.
- The PI code is derived from the call sign with the RBDS rule, which is what an RDS receiver
  would display: HD Radio does not transmit it and no database publishes it. Three-letter call
  signs are an exception table in the standard rather than a formula, and Canadian and Mexican
  PI codes are assigned rather than derived, so those are left blank instead of guessed.
- The licensee, the class, the HAAT and the transmitter coordinates are in the tooltip, so the
  new fields cost the artwork a single 37 px row.
- `radio-locator.com` and `fmlist.org` are deliberately **not** queried. The pages that carry
  slogan, power and location are `Disallow`ed to every user agent in their `robots.txt`
  (`/info` and `/cgi-bin/pat` on radio-locator, `/export/` and `/demoapi/` on fmlist), and
  fmlist has no unauthenticated API. A project test fails if either host reappears in the
  source.

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
