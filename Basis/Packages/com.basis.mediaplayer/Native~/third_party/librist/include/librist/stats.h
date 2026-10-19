#ifndef LIBRIST_STATS_H
#define LIBRIST_STATS_H

#include "common.h"
#include "headers.h"

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif


struct rist_stats_sender_peer
{
	/* cname */
	char cname[RIST_MAX_STRING_SHORT];
	/* internal peer id */
	uint32_t peer_id;
	/* avg bandwidth calculation */
	size_t bandwidth;
	/* bandwidth devoted to retries */
	size_t retry_bandwidth;
	/* num sent packets */
	uint64_t sent;
	/* num received packets */
	uint64_t received;
	/* retransmitted packets */
	uint64_t retransmitted;
	/* quality: Q = (sent * 100.0) / sent + bloat_skipped + bandwidth_skipped + retransmit_skipped + retransmitted */
	double quality;
	/* current RTT */
	uint32_t rtt;
};

struct rist_stats_receiver_flow
{
	/* peer count */
	uint32_t peer_count;
	/* combined peer cnames */
	char cname[RIST_MAX_STRING_LONG];
	/* flow id (set by senders) */
	uint32_t flow_id;
	/* flow status */
	int status;
	/* avg bandwidth calculation */
	size_t bandwidth;
	/* bandwidth devoted to retries */
	size_t retry_bandwidth;
	/* num sent packets */
	uint64_t sent;
	/* num received packets */
	uint64_t received;
	/* missing, including reordered */
	uint32_t missing;
	/* reordered */
	uint32_t reordered;
	/* total recovered */
	uint32_t recovered;
	/* recovered on the first retry */
	uint32_t recovered_one_retry;
	/* lost packets */
	uint32_t lost;
	/* quality: Q = (received * 100.0) / received + missing */
	double quality;
	/* packet inter-arrival time (microseconds) */
	uint64_t min_inter_packet_spacing;
	uint64_t cur_inter_packet_spacing;
	uint64_t max_inter_packet_spacing;
	/* avg rtt all non dead peers */
	uint32_t rtt;
};

enum rist_stats_type
{
	RIST_STATS_SENDER_PEER,
	RIST_STATS_RECEIVER_FLOW
};

#define RIST_STATS_VERSION (0)

struct rist_stats
{
	uint32_t json_size;
	char *stats_json;
	uint16_t version;
	enum rist_stats_type stats_type;
	union {
		struct rist_stats_sender_peer sender_peer;
		struct rist_stats_receiver_flow receiver_flow;
	} stats;
};


/**
 * @brief Set callback for receiving stats structs
 *
 * @param ctx RIST context
 * @param statsinterval interval between stats reporting
 * @param stats_cb Callback function that will be called. The json char pointer must be free()'d when you are finished.
 * @param arg extra arguments for callback function
 */
RIST_API int rist_stats_callback_set(struct rist_ctx *ctx, int statsinterval, int (*stats_cb)(void *arg, const struct rist_stats *stats_container), void *arg);

/**
 * @brief Free the rist_stats structure memory allocations
 *
 * @return 0 on success or non-zero on error.
 */
RIST_API int rist_stats_free(const struct rist_stats *stats_container);

#ifdef __cplusplus
}
#endif


#endif /* LIBRIST_STATS_H */
