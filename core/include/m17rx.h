/*
 * libm17rx - M17 receiver core: complex baseband in, decoded voice, link
 * information, packets and signal measurements out.
 *
 * Copyright 2026 Wojciech Kaczmarski, SP5WWP
 * SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception
 *
 * Threading: a receiver instance is not thread-safe. Calls on one instance
 * must not overlap; the caller serialises them (the SDR# plugin takes a lock
 * around every call). Separate instances are independent.
 *
 * All structs are plain data with fixed-size fields so they can be marshalled
 * directly (C# P/Invoke, LayoutKind.Sequential, default packing). Their layout
 * is the same on Linux x64 and Windows x86/x64; m17rx_struct_size() lets a
 * caller check its own definitions against the library at run time.
 */
#ifndef M17RX_H
#define M17RX_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
    #if defined(M17RX_BUILD)
        #define M17RX_API __declspec(dllexport)
    #else
        #define M17RX_API __declspec(dllimport)
    #endif
    #define M17RX_CALL __cdecl
#else
    #define M17RX_API __attribute__((visibility("default")))
    #define M17RX_CALL
#endif

#define M17RX_API_VERSION 2

/* ------------------------------------------------------------------ */
/* Link information                                                    */
/* ------------------------------------------------------------------ */

#define M17RX_DT_RESERVED   0
#define M17RX_DT_DATA       1
#define M17RX_DT_VOICE      2   /* Codec2 3200 */
#define M17RX_DT_VOICE_DATA 3   /* Codec2 1600 + data (audio not decoded in this version) */

#define M17RX_ENC_NONE      0
#define M17RX_ENC_SCRAMBLER 1
#define M17RX_ENC_AES       2
#define M17RX_ENC_RESERVED  3

/* m17rx_link_t.decrypt: what the receiver can do with the payload */
#define M17RX_DEC_CLEAR      0  /* not encrypted */
#define M17RX_DEC_OK         1  /* decrypted with the configured AES key / scrambler seed */
#define M17RX_DEC_NO_KEY     2  /* encrypted, no key of the size the TYPE field asks for: muted */
#define M17RX_DEC_UNSUPPORTED 3 /* reserved encryption type or subtype: muted */

#define M17RX_META_NONE     0   /* no META, or META not meaningful (encrypted) */
#define M17RX_META_TEXT     1
#define M17RX_META_GNSS     2
#define M17RX_META_ECD      3   /* Extended Callsign Data */
#define M17RX_META_RAW      4   /* reserved type: shown as hex */

typedef struct
{
    int32_t  valid;              /* 1 when a CRC-valid LSF has been received */
    int32_t  from_lich;          /* 1 if it was assembled from LICH (late join) */
    char     src[12];            /* decoded callsigns, NUL-terminated */
    char     dst[12];
    uint16_t type;               /* raw TYPE field */
    uint16_t _pad0;
    int32_t  stream;             /* 1 stream mode, 0 packet mode */
    int32_t  data_type;          /* M17RX_DT_* */
    int32_t  enc_type;           /* M17RX_ENC_* */
    int32_t  enc_subtype;        /* key size / LFSR length / META type */
    int32_t  can;                /* Channel Access Number 0..15 */
    int32_t  signed_stream;      /* TYPE bit 11 */
    uint8_t  meta[14];           /* raw META field */
    uint16_t _pad1;

    /* Decoded META */
    int32_t  meta_kind;          /* M17RX_META_* */
    char     meta_text[64];      /* Text Data, all blocks received so far, UTF-8 */
    int32_t  meta_text_complete; /* 1 when every block of the message has arrived */
    char     ecd_cf1[12];        /* Extended Callsign Data: originator */
    char     ecd_cf2[12];        /*   reflector / second field, may be empty */
    int32_t  gnss_valid_pos;
    int32_t  gnss_valid_alt;
    int32_t  gnss_valid_speed;
    int32_t  gnss_station;       /* 0 fixed, 1 mobile, 2 handheld, ... */
    int32_t  gnss_source;
    double   gnss_lat;           /* degrees */
    double   gnss_lon;
    float    gnss_alt_m;
    float    gnss_speed_kmh;
    int32_t  gnss_bearing_deg;

    int32_t  decrypt;            /* M17RX_DEC_* for this receiver's keys */
} m17rx_link_t;

/* ------------------------------------------------------------------ */
/* Live status and signal measurements                                 */
/* ------------------------------------------------------------------ */

#define M17RX_STATE_SEARCH  0    /* looking for a syncword */
#define M17RX_STATE_STREAM  1    /* receiving a stream */
#define M17RX_STATE_PACKET  2    /* receiving a packet */

typedef struct
{
    int32_t  state;              /* M17RX_STATE_* */
    int32_t  locked;             /* frame sync held */
    int32_t  last_fn;            /* last stream Frame Number (with EOS bit) or -1 */
    int32_t  frames;             /* data frames in the current transmission */
    int32_t  frames_lost;        /* gaps in FN in the current transmission */
    float    duration_s;         /* of the current transmission */
    int32_t  lich_chunks;        /* bitmap of LICH chunks collected (0x3F = all) */

    /* Measurements, smoothed over recent frames. Valid when meas_valid != 0. */
    int32_t  meas_valid;
    float    evm_rms_pct;        /* RMS EVM, % of the RMS symbol level */
    float    evm_peak_pct;       /* peak EVM over the last frame, same reference */
    float    mer_db;
    float    ber_pre_fec;        /* hard-decision bit errors before FEC, 0..1 */
    float    viterbi_ber;        /* corrected bits / payload bits, last frame */
    float    eye_opening_pct;    /* worst of the three eyes, 100 = ideal */
    float    deviation_hz;       /* outer symbol (+/-3) deviation */
    float    inner_outer_ratio;  /* nominal 1/3 */
    float    freq_offset_hz;     /* carrier offset seen by the discriminator */
    float    clock_ppm;          /* symbol clock error vs 4800 Bd */
    float    timing_frac_sym;    /* syncword position correction, symbols */
    float    level_mean[4];      /* -3, -1, +1, +3 after normalisation */
    float    level_sigma[4];
    float    sync_quality;       /* correlation of the last syncword, 0..1 */
    int32_t  evm_reference;      /* 0 = re-encoded after FEC, 1 = nearest level */
    int32_t  polarity_hint;      /* inverted LSFs seen recently: > 0 means try the invert setting */

    float    input_rate;         /* Hz, as set */
    uint32_t audio_queued;       /* 8 kHz samples waiting in the audio buffer */

    m17rx_link_t link;           /* current transmission */
} m17rx_status_t;

/* ------------------------------------------------------------------ */
/* Events                                                              */
/* ------------------------------------------------------------------ */

#define M17RX_EV_NONE         0
#define M17RX_EV_STREAM_START 1  /* link valid (from LSF or LICH) */
#define M17RX_EV_LINK_UPDATE  2  /* META or other LSF content changed */
#define M17RX_EV_STREAM_END   3  /* EOS bit, EoT marker or signal lost */
#define M17RX_EV_PACKET       4  /* complete packet with valid CRC */
#define M17RX_EV_PACKET_BAD   5  /* packet ended but CRC or framing failed */

#define M17RX_END_EOS       1
#define M17RX_END_EOT       2
#define M17RX_END_LOST      3

#define M17RX_PKT_MAX       (33 * 25)

/* m17rx_event_t.sig_state (STREAM_END) */
#define M17RX_SIG_NONE       0  /* stream not signed */
#define M17RX_SIG_VALID      1  /* verified with the sender's public key (sig_key: its name) */
#define M17RX_SIG_INVALID    2  /* all frames received, the sender's public key does not verify it */
#define M17RX_SIG_NO_KEY     3  /* no public key stored under the sender's callsign */
#define M17RX_SIG_INCOMPLETE 4  /* frames missed or joined late: the digest is unknown */
#define M17RX_SIG_MISSING    5  /* the stream ended before all four signature frames */

typedef struct
{
    int32_t  kind;               /* M17RX_EV_* */
    int32_t  end_reason;         /* M17RX_END_* for STREAM_END */
    int32_t  frames;             /* STREAM_END: data frames received */
    int32_t  frames_lost;
    float    duration_s;
    float    evm_rms_pct;        /* STREAM_END: average over the transmission */
    int32_t  sig_state;          /* STREAM_END: M17RX_SIG_* */
    char     sig_key[32];        /* M17RX_SIG_VALID: name of the key that verified it */
    m17rx_link_t link;

    /* PACKET */
    int32_t  protocol;           /* first byte of the packet data (0x05 = SMS) */
    int32_t  length;             /* bytes in data[], without the CRC */
    uint8_t  data[M17RX_PKT_MAX];
    char     sms[M17RX_PKT_MAX + 1]; /* protocol 0x05: the text, NUL-terminated */
} m17rx_event_t;

/* ------------------------------------------------------------------ */
/* Settings                                                            */
/* ------------------------------------------------------------------ */

typedef struct
{
    float    sync_threshold;     /* syncword correlation to acquire, default 0.90 */
    float    track_threshold;    /* correlation to keep lock, default 0.55 */
    int32_t  invert;             /* swap symbol polarity */
    float    viterbi_mute;       /* mute voice frames above this corrected-bit ratio, default 0.08 */
    int32_t  eye_source;         /* 0 after RRC, 1 raw discriminator */
} m17rx_config_t;

typedef struct m17rx m17rx_t;

/* Lifetime */
M17RX_API int        M17RX_CALL m17rx_api_version(void);
M17RX_API const char*M17RX_CALL m17rx_version(void);
M17RX_API m17rx_t*   M17RX_CALL m17rx_create(void);
M17RX_API void       M17RX_CALL m17rx_destroy(m17rx_t* rx);
M17RX_API void       M17RX_CALL m17rx_reset(m17rx_t* rx);

/* sizeof the public structs: 0 link, 1 status, 2 event, 3 config; -1 otherwise */
M17RX_API int        M17RX_CALL m17rx_struct_size(int which);

/* Configuration */
M17RX_API void       M17RX_CALL m17rx_default_config(m17rx_config_t* cfg);
M17RX_API void       M17RX_CALL m17rx_set_config(m17rx_t* rx, const m17rx_config_t* cfg);
M17RX_API int        M17RX_CALL m17rx_set_input_rate(m17rx_t* rx, double fs_hz); /* 8 kHz .. 2.4 MHz */

/* Keys (spec 2.0.x, compatible with gr-m17). Changes apply from the next frame.
   AES: len 0 (none), 16, 24 or 32 bytes. Scrambler: bits 0 (none), 8, 16 or 24.
   Public keys (secp256r1): 64 bytes X||Y, 65 bytes 04||X||Y or 33 bytes compressed, named by
   the callsign they belong to: a signature is checked only with the key named as its source;
   add returns 0, or -1 if the key is not a valid point (or the keyring is full). */
#define M17RX_MAX_PUBKEYS 64
M17RX_API int        M17RX_CALL m17rx_set_aes_key(m17rx_t* rx, const uint8_t* key, int len);
M17RX_API int        M17RX_CALL m17rx_set_scrambler_seed(m17rx_t* rx, uint32_t seed, int bits);
M17RX_API void       M17RX_CALL m17rx_clear_public_keys(m17rx_t* rx);
M17RX_API int        M17RX_CALL m17rx_add_public_key(m17rx_t* rx, const char* name, const uint8_t* key, int len);

/* Processing: n complex samples, interleaved I,Q, any scale */
M17RX_API int        M17RX_CALL m17rx_process_iq(m17rx_t* rx, const float* iq, int n);
/* Alternative input: FM-discriminator output in Hz (already demodulated), at the input rate */
M17RX_API int        M17RX_CALL m17rx_process_fm(m17rx_t* rx, const float* hz, int n);

/* Outputs */
M17RX_API int        M17RX_CALL m17rx_read_audio(m17rx_t* rx, int16_t* pcm8k, int max); /* returns samples */
M17RX_API int        M17RX_CALL m17rx_next_event(m17rx_t* rx, m17rx_event_t* ev);       /* 1 = got one */
M17RX_API void       M17RX_CALL m17rx_get_status(m17rx_t* rx, m17rx_status_t* st);

/* Signal views. Eye: up to max_traces traces of M17RX_EYE_POINTS points each,
   spanning two symbols (-1 .. +1 around a symbol instant), normalised so the
   ideal levels are -3, -1, +1, +3. Newest last. Returns the number of traces. */
#define M17RX_EYE_POINTS 21
#define M17RX_HIST_BINS  64      /* histogram from -4.5 to +4.5 */
#define M17RX_EVM_HISTORY 64

M17RX_API int        M17RX_CALL m17rx_get_eye(m17rx_t* rx, float* out, int max_traces);
M17RX_API void       M17RX_CALL m17rx_get_histogram(m17rx_t* rx, float out[M17RX_HIST_BINS]);
M17RX_API int        M17RX_CALL m17rx_get_evm_history(m17rx_t* rx, float out[M17RX_EVM_HISTORY]); /* per frame, oldest first */

#ifdef __cplusplus
}
#endif

#endif /* M17RX_H */
