# M17 Decoder for SDR#

An M17 receiver plugin for SDR# (v1921 / v1922, .NET 9 and .NET 10 builds,
32- and 64-bit). It decodes M17 voice and packets from any receiver SDR# supports,
plays the voice through SDR#'s audio path and shows link information and signal
measurements.

## What it does

- **Voice:** Codec2 3200 streams, played through SDR#'s audio.
- **Link information:** source, destination, CAN, data type, encryption, and
  signature status. When reception starts mid-stream, the link info is rebuilt from LICH.
- **META:** text (multi-block messages are assembled), GNSS position (with a
  copy button) and Extended Callsign Data.
- **Packets:** SMS (protocol 0x05) is shown as text. Other protocols are logged
  with their protocol name and size.
- **Signal tab:**
  - Eye diagram after the RRC filter, with 1, 2 or 4 frames of persistence and a freeze.
  - Histogram of the symbol levels.
  - EVM and MER: each decoded frame is re-encoded to give the exact
    transmitted symbols.
  - Eye opening and pre-FEC BER.
  - Deviation, inner/outer level ratio, carrier offset and symbol clock error.
- **Log:** every transmission and packet exportable as CSV and optionally
  appended to a CSV file.
- **Decryption** (as per spec 2.0.x, interoperable with gr-m17): AES-128/192/256 and the
  8, 16 and 24-bit scramblers. The scrambler keystream is rebuilt from the frame
  number after a lost frame or a late join. Without a matching key the audio is
  muted.
- **Signatures:** ECDSA over secp256r1 (micro-ecc), checked against the sender's key. The result shows
  when the transmission ends: valid (with the key's name), invalid, no public
  keys, not checkable (frames were missed or reception started late), or not
  received.
- **Setup:** receiver setup (NFM, 12.5 kHz, squelch off), and a
  warning when the signal's polarity is inverted.

Not in this version:
- Codec2 1600 (voice + data) audio.

## Keys

On the Settings tab, under **Keys**:
- **AES key:** 32, 48 or 64 hex digits. The size must match the transmission's
  (AES-128/192/256).
- **Scrambler seed:** 2, 4 or 6 hex digits (8, 16 or 24-bit LFSR).
- **Public keys:** a name (usually the callsign) and the P-256 public key, either
  as 128 hex digits (X||Y, as gr-m17's coder prints with Debug on), 130 digits
  with the 04 prefix, 66 digits compressed, or PEM (`openssl ec -pubout`). The
  receiver never needs a private key.

The AES key and seed are stored with Windows DPAPI (readable only by the same
Windows user) in `%APPDATA%\SDRSharp\M17Decoder\settings.json`. Public keys
are stored as entered.

## How it works

SDR# hands the plugin the VFO's decimated, filtered IQ through
`ProcessorType.DecimatedAndFilteredIQ`, at whatever rate SDR# picks.

In the core:
1. An FM discriminator turns the IQ stream into instantaneous frequency.
2. A resampler converts that to 48 kHz.
3. An RRC matched filter shapes it for symbol decisions.
4. Every 40 ms frame starts with a known syncword. The core finds it by
   normalised correlation (so deviation and carrier offset don't matter) and
   refines its position to a fraction of a sample.
5. A least-squares fit gives the gain (deviation) and DC (carrier offset). The
   fit starts from the syncword.
6. libm17 decodes the frame, and Codec2-mod decodes the voice.

To avoid false locks on noise, stream and packet frames only count once the next
frame's syncword turns up exactly 40 ms later. LSF frames are trusted on their
CRC.

The plugin replaces SDR#'s filtered audio (`ProcessorType.FilteredAudioOutput`)
with the decoded voice. It goes through a 150 ms jitter buffer with latency
capped at 600 ms, and is resampled to SDR#'s audio rate. The plugin works out by
itself whether that audio is mono or stereo.

## Building (Windows)

Needs the .NET 9 or 10 SDK and airspy's **SDR# SDK for Plugin Developers**. The
SDK's licence doesn't allow it to be redistributed, so it is not included:
download it from airspy.com and point the build at its `lib` folder.

```
.\build-plugin.ps1 -SdrSharpLib "C:\path\to\sdrplugins\lib" -Install "C:\path\to\SDRSharp\Plugins"
```

This builds `plugin\SDRSharp.M17.csproj` (target `net9.0-windows`, which runs on
.NET 9 and 10 hosts) and assembles `dist\M17Decoder\`. With `-Install`, it also
copies that folder into SDR#'s `Plugins` folder. Use `-Framework net10.0-windows`
to build for .NET 10 only.

### Running

Restart SDR#, open **M17 Decoder** from the plugin menu, and on its Settings tab
press **Set up receiver for M17** (NFM, 12.5 kHz filter, squelch off). Then tune
to an M17 signal.

## Licence

GPL-2.0-or-later with an additional permission for SDR# (see `COPYING` and
`SDRSHARP-EXCEPTION.txt`). Codec2-mod in `core/third_party/codec2-mod` is
LGPL-2.1. micro-ecc in `core/third_party/micro-ecc` is BSD-2-Clause, its notice
is in `THIRD-PARTY-NOTICES.txt`, which ships with the plugin.
