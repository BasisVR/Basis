#ifndef LIBRIST_SENDER_H
#define LIBRIST_SENDER_H

#include "common.h"
#include "logging.h"
#include "headers.h"

#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif


/**
 * @brief Helper function used to create valid random 32 bit flow_id.
 *
 * Use this function when you want to generate a valid random flow_id.
 *
 * @return random uint32_t number that complies with the flow_id rules
 */
RIST_API uint32_t rist_flow_id_create(void);

/**
 * @brief Create Sender
 *
 * Create a RIST sender instance
 *
 * @param[out] ctx a context representing the sender instance
 * @param profile RIST profile
 * @param flow_id Flow ID, use 0 to delegate creation of flow_id to lib
 * @param logging_settings Struct containing logging settings
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_sender_create(struct rist_ctx **ctx, enum rist_profile profile,
				uint32_t flow_id, struct rist_logging_settings *logging_settings);

/**
 * @brief Get RIST NULL Packet deletion status
 *
 * Returns the current status of NULL Packet deletion (NPD).
 *
 * @param ctx RIST sender ctx
 * @param[out] npd current NPD status, true if NPD is enabled.
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_sender_npd_get(const struct rist_ctx *ctx, bool *npd);

/**
 * @brief Enable RIST NULL Packet deletion
 *
 *  Enables deletion of NULL packets, packets are modified on submission to
 *  the libRIST library, so this only affects packets inserted after enabling
 *  NPD.
 * @param ctx RIST sender ctx
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_sender_npd_enable(struct rist_ctx *ctx);

/**
 * @brief Disable RIST NULL Packet deletion
 *
 *  Disables deletion of NULL packets, packets are modified on submission to
 *  the libRIST library, so this only affects packets inserted after enabling
 *  NPD.
 * @param ctx RIST sender ctx
 * @return 0 on success, -1 in case of error.
 */
RIST_API int rist_sender_npd_disable(struct rist_ctx *ctx);

/**
 * @brief Retrieve the current flow_id value
 *
 * Retrieve the current flow_id value
 *
 * @param ctx RIST sender context
 * @param flow_id pointer to your flow_id variable
 * @return 0 on success, -1 on error
 */
RIST_API int rist_sender_flow_id_get(struct rist_ctx *ctx, uint32_t *flow_id);

/**
 * @brief Change the flow_id value
 *
 * Change the flow_id value
 *
 * @param ctx RIST sender context
 * @param flow_id new flow_id
 * @return 0 on success, -1 on error
 */
RIST_API int rist_sender_flow_id_set(struct rist_ctx *ctx, uint32_t flow_id);

/**
 * @brief Write data into a librist packet.
 *
 * One sender can send write data into a librist packet.
 *
 * @param ctx RIST sender context
 * @param data_block pointer to the rist_data_block structure
 * the ts_ntp will be populated by the lib if a value of 0 is passed
 * @return number of written bytes on success, -1 in case of error.
 */
RIST_API int rist_sender_data_write(struct rist_ctx *ctx, const struct rist_data_block *data_block);


#ifdef __cplusplus
}
#endif

#endif /* LIBRIST_SENDER_H */
