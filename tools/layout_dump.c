/* Prints the layout of libm17rx's public structs as "struct field offset size count kind" lines.
   Used by gen_interop.py to generate the C# definitions. SPDX-License-Identifier: GPL-2.0-or-later WITH AdditionRef-SDRSharp-exception */
#include <stddef.h>
#include <stdio.h>
#include "m17rx.h"
#define F(S, f, kind, n) printf("%s %s %zu %zu %d %s\n", #S, #f, offsetof(S, f), sizeof(((S *)0)->f), n, kind);
int main(void)
{
    printf("size m17rx_link_t %zu\nsize m17rx_status_t %zu\nsize m17rx_event_t %zu\nsize m17rx_config_t %zu\n",
           sizeof(m17rx_link_t), sizeof(m17rx_status_t), sizeof(m17rx_event_t), sizeof(m17rx_config_t));
    F(m17rx_link_t, valid, "int", 1) F(m17rx_link_t, from_lich, "int", 1) F(m17rx_link_t, src, "str", 12) F(m17rx_link_t, dst, "str", 12)
    F(m17rx_link_t, type, "ushort", 1) F(m17rx_link_t, stream, "int", 1) F(m17rx_link_t, data_type, "int", 1) F(m17rx_link_t, enc_type, "int", 1)
    F(m17rx_link_t, enc_subtype, "int", 1) F(m17rx_link_t, can, "int", 1) F(m17rx_link_t, signed_stream, "int", 1) F(m17rx_link_t, meta, "bytes", 14)
    F(m17rx_link_t, meta_kind, "int", 1) F(m17rx_link_t, meta_text, "str", 64) F(m17rx_link_t, meta_text_complete, "int", 1)
    F(m17rx_link_t, ecd_cf1, "str", 12) F(m17rx_link_t, ecd_cf2, "str", 12) F(m17rx_link_t, gnss_valid_pos, "int", 1) F(m17rx_link_t, gnss_valid_alt, "int", 1)
    F(m17rx_link_t, gnss_valid_speed, "int", 1) F(m17rx_link_t, gnss_station, "int", 1) F(m17rx_link_t, gnss_source, "int", 1)
    F(m17rx_link_t, gnss_lat, "double", 1) F(m17rx_link_t, gnss_lon, "double", 1) F(m17rx_link_t, gnss_alt_m, "float", 1)
    F(m17rx_link_t, gnss_speed_kmh, "float", 1) F(m17rx_link_t, gnss_bearing_deg, "int", 1)
    F(m17rx_link_t, decrypt, "int", 1)
    F(m17rx_status_t, state, "int", 1) F(m17rx_status_t, locked, "int", 1) F(m17rx_status_t, last_fn, "int", 1) F(m17rx_status_t, frames, "int", 1)
    F(m17rx_status_t, frames_lost, "int", 1) F(m17rx_status_t, duration_s, "float", 1) F(m17rx_status_t, lich_chunks, "int", 1)
    F(m17rx_status_t, meas_valid, "int", 1) F(m17rx_status_t, evm_rms_pct, "float", 1) F(m17rx_status_t, evm_peak_pct, "float", 1)
    F(m17rx_status_t, mer_db, "float", 1) F(m17rx_status_t, ber_pre_fec, "float", 1) F(m17rx_status_t, viterbi_ber, "float", 1)
    F(m17rx_status_t, eye_opening_pct, "float", 1) F(m17rx_status_t, deviation_hz, "float", 1) F(m17rx_status_t, inner_outer_ratio, "float", 1)
    F(m17rx_status_t, freq_offset_hz, "float", 1) F(m17rx_status_t, clock_ppm, "float", 1) F(m17rx_status_t, timing_frac_sym, "float", 1)
    F(m17rx_status_t, level_mean, "floats", 4) F(m17rx_status_t, level_sigma, "floats", 4) F(m17rx_status_t, sync_quality, "float", 1)
    F(m17rx_status_t, evm_reference, "int", 1) F(m17rx_status_t, polarity_hint, "int", 1) F(m17rx_status_t, input_rate, "float", 1)
    F(m17rx_status_t, audio_queued, "uint", 1) F(m17rx_status_t, link, "link", 1)
    F(m17rx_event_t, kind, "int", 1) F(m17rx_event_t, end_reason, "int", 1) F(m17rx_event_t, frames, "int", 1) F(m17rx_event_t, frames_lost, "int", 1)
    F(m17rx_event_t, duration_s, "float", 1) F(m17rx_event_t, evm_rms_pct, "float", 1)
    F(m17rx_event_t, sig_state, "int", 1) F(m17rx_event_t, sig_key, "str", 32) F(m17rx_event_t, link, "link", 1)
    F(m17rx_event_t, protocol, "int", 1) F(m17rx_event_t, length, "int", 1) F(m17rx_event_t, data, "bytes", M17RX_PKT_MAX) F(m17rx_event_t, sms, "str", M17RX_PKT_MAX + 1)
    F(m17rx_config_t, sync_threshold, "float", 1) F(m17rx_config_t, track_threshold, "float", 1) F(m17rx_config_t, invert, "int", 1)
    F(m17rx_config_t, viterbi_mute, "float", 1) F(m17rx_config_t, eye_source, "int", 1)
    return 0;
}
