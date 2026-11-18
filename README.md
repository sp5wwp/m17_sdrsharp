# M17 Decoder for SDR#

An M17 receiver plugin for SDR# (v1921 / v1922, .NET 10 builds, 32- and
64-bit). It decodes M17 voice and packets from any receiver SDR# supports,
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
- **Signatures** (M17 Specification 2.1.0): a SHA-256 hash over the stream and its
  Authentication Frame, signed with ECDSA over secp256r1 (micro-ecc), checked with the
  public key stored under the signer's callsign. The result shows when the transmission
  ends: valid (with the key's name), invalid, no public key, not checkable (frames were
  missed or reception started late), or not received. The decoder also shows the signer,
  the destination and the time of signing, and warns when the signer is not the station
  the stream names or when the time of signing is more than 300 s off the PC's clock.
  Signatures in the older (XOR digest) format do not verify.
- **Setup:** receiver setup (NFM, 12.5 kHz, squelch off), and a
  warning when the signal's polarity is inverted.

Not in this version:
- Codec2 1600 (voice + data) audio.

## Keys

On the Settings tab, under **Keys**:
- **AES key:** 32, 48 or 64 hex digits. The size must match the transmission's
  (AES-128/192/256).
- **Scrambler seed:** 2, 4 or 6 hex digits (8, 16 or 24-bit LFSR).
- **Public keys:** a name (the signer's callsign) and the P-256 public key, either
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

## Installing

Download `M17Decoder.zip` from the Releases page and extract it into SDR#'s
`Plugins` folder, so that you get `Plugins\M17Decoder\SDRSharp.M17.dll`. The
release is built for .NET 10 and does not load in SDR# builds that run on .NET 9.

### Running

Restart SDR#, open **M17 Decoder** from the plugin menu, and on its Settings tab
press **Set up receiver for M17** (NFM, 12.5 kHz filter, squelch off). Then tune
to an M17 signal.

## Building (Windows)

Needs the .NET 10 SDK and airspy's **SDR# VDK** (the plugin development kit,
formerly the "SDR# SDK for Plugin Developers"). The VDK's licence doesn't allow
it to be redistributed, so it is not included: download it from airspy.com and
point the build at the folder you unpacked it to.

```
.\build-plugin.ps1 -SdrSharpLib "C:\path\to\vdk" -Framework net10.0-windows -Install "C:\path\to\SDRSharp\Plugins"
```

The script finds `SDRSharp.Common.dll` and `SDRSharp.Radio.dll` itself (in the
VDK they are in `lib\ref`). It builds `plugin\SDRSharp.M17.csproj` for
`net10.0-windows` and assembles `dist\M17Decoder\` and `dist\M17Decoder.zip`,
the file to attach to a release. With `-Install`, it also copies that folder into
SDR#'s `Plugins` folder.

The VDK's libraries are built for .NET 10, so a `net9.0-windows` build against them
fails (error CS1705). A .NET 9 build needs the libraries of the older SDK.

If PowerShell refuses to run the script, allow scripts for that window first:
`Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass`.

## Licence

GPL-2.0-or-later with an additional permission for SDR# (see `COPYING` and
`SDRSHARP-EXCEPTION.txt`). Codec2-mod in `core/third_party/codec2-mod` is
LGPL-2.1. micro-ecc in `core/third_party/micro-ecc` is BSD-2-Clause, its notice
is in `THIRD-PARTY-NOTICES.txt`, which ships with the plugin. sha-2 (SHA-256) in
`core/third_party/sha-2` is Unlicense or 0BSD; `core/src/m17sig.c` is MIT.