/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef RIST_LOGGING_H
#define RIST_LOGGING_H

#include "headers.h"
#include "common.h"
#include <stdio.h>

#ifdef __cplusplus
extern "C" {
#endif

enum rist_log_level
{
	RIST_LOG_DISABLE = -1,
	RIST_LOG_ERROR = 3,
	RIST_LOG_WARN = 4,
	RIST_LOG_NOTICE = 5,
	RIST_LOG_INFO = 6,
	RIST_LOG_DEBUG = 7,
	RIST_LOG_SIMULATE = 100,
};

/**
 * The recommended way to use the logging settings is to stack/heap alloc
 * and initialize via LOGGING_SETTINGS_INITIALIZER, and set it's members.
 *
 * Then call rist_logging_set_global to have the settings copied in for the
 * global log settings (used by udpsocket_ functions).
 *
 * When logging is initialized with either user_data, an udp socket or
 * filestream it's important to call rist_logging_unset_global if those
 * resources are going to be freed, to prevent a use after free, or on exit
 * when using UDP logging to prevent fd's leaking.
 **/

#if !defined(__cplusplus) || __cplusplus >= 202002L
#define LOGGING_SETTINGS_INITIALIZER        \
    {                                       \
        .log_level = RIST_LOG_DISABLE,      \
        .log_cb = NULL,                     \
        .log_cb_arg = NULL,                 \
        .log_socket = -1,                   \
        .log_stream = NULL,                 \
    }
#else
#define LOGGING_SETTINGS_INITIALIZER        \
    {                                       \
        RIST_LOG_DISABLE,                   \
        NULL,                               \
        NULL,                               \
        -1,                                 \
        NULL,                               \
    }
#endif

struct rist_logging_settings {
	enum rist_log_level log_level;///<minimum log level
	/**
	 * @brief Log callback function
	 * When set this function is called by the libRIST library whenever a
	 * log message is available. NULL to disable log callback.
	 *
	 * The log callback function can be called from multiple threads, so it
	 * needs to be thread-safe.
	 *
	 * @param arg, user data
	 * @param level log level
	 * @param msg log message
	 */
    int (*log_cb)(void* arg, enum rist_log_level, const char* msg);
	void *log_cb_arg;///< user data passed to log callback function
	int log_socket;///< socket to which log messages are written via send call
	FILE *log_stream;///< FILE to which log messages are written via fputs call
};

/* public interfaces in logging.c */
RIST_API void rist_log(struct rist_logging_settings *logging_settings, enum rist_log_level level, const char *format, ...);

/**
 * @brief populates and creates logging settings struct with log settings
 *
 * This also sets the global logging settings if they were not set before.
 *
 * @param logging_settings if pointed to pointer is NULL struct will be allocated, otherwise pointed to struct will have it's values updated by given values, closing and opening sockets as needed.
 * @param log_level minimum log level to report
 * @param log_cb log callback , NULL to disable
 * @param cb_args user data passed to log callback function, NULL when unused
 * @param address destination address for UDP log messages, NULL when unused
 * @param logfp log file to write to, NULL when unused
 **/
RIST_API int rist_logging_set(struct rist_logging_settings **logging_settings, enum rist_log_level log_level, int (*log_cb)(void *arg, enum rist_log_level, const char *msg), void *cb_arg, char *address, FILE *logfp);

/**
 * @brief Set global log settings
 * Set the global log settings that are used by the udpsocket_ functions
 * @param logging_settings struct containing log settings from which log settings will be copied from
 * @return 0 for success
 **/
RIST_API int rist_logging_set_global(struct rist_logging_settings *logging_settings);

/**
 * @brief Unset global log settings
 *
 * This will unset the global log settings, closing any dupped socket as needed.
 **/
RIST_API void rist_logging_unset_global(void);

/**
 * @brief Free the rist_logging_settings structure memory allocation
 *
 * @return 0 on success or non-zero on error.
 */
RIST_DEPRECATED RIST_API int rist_logging_settings_free(const struct rist_logging_settings **logging_settings);
RIST_API int rist_logging_settings_free2(struct rist_logging_settings **logging_settings);

#ifdef __cplusplus
}
#endif

#endif
