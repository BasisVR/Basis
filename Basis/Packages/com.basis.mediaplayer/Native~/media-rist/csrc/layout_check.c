/* Exports sizeof/offsetof of the librist structs media-rist declares by hand,
 * so a Rust test can assert the declared layout matches the pinned headers on
 * every build (the equivalent of bindgen's layout tests). */

#include <librist/librist.h>
#include <stddef.h>

size_t bm_rist_sizeof_peer_config(void) { return sizeof(struct rist_peer_config); }
size_t bm_rist_offsetof_pc_initiate_conn(void) { return offsetof(struct rist_peer_config, initiate_conn); }
size_t bm_rist_offsetof_pc_address(void) { return offsetof(struct rist_peer_config, address); }
size_t bm_rist_offsetof_pc_physical_port(void) { return offsetof(struct rist_peer_config, physical_port); }
size_t bm_rist_offsetof_pc_secret(void) { return offsetof(struct rist_peer_config, secret); }
size_t bm_rist_offsetof_pc_key_size(void) { return offsetof(struct rist_peer_config, key_size); }
size_t bm_rist_offsetof_pc_srp_password(void) { return offsetof(struct rist_peer_config, srp_password); }

size_t bm_rist_sizeof_data_block(void) { return sizeof(struct rist_data_block); }
size_t bm_rist_offsetof_db_payload(void) { return offsetof(struct rist_data_block, payload); }
size_t bm_rist_offsetof_db_payload_len(void) { return offsetof(struct rist_data_block, payload_len); }
size_t bm_rist_offsetof_db_flags(void) { return offsetof(struct rist_data_block, flags); }
size_t bm_rist_offsetof_db_ref(void) { return offsetof(struct rist_data_block, ref); }

size_t bm_rist_sizeof_logging_settings(void) { return sizeof(struct rist_logging_settings); }
size_t bm_rist_offsetof_ls_log_stream(void) { return offsetof(struct rist_logging_settings, log_stream); }
