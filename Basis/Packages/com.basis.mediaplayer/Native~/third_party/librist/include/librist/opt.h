/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_OPT_H
#define LIBRIST_OPT_H

#include "common.h"
#include "headers.h"

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif


/**
 * @brief Thread callback function
 * 
 * The thread callback function is called whenever a thread is created or (about to be) destroyed. This can be used 
 * to apply cpu sets to the thread, or set fine grained priorities.
 * 
 * @param handle, OS specific thread handle, on POSIX systems this will be a pointer to pthread_t on Windows systems
 * 				  this will be a pointer to a pseudo thread handle. The handle is invalidated after the callback is
 * 				  called a second time with created set to false.
 * @param type Not used for now, it is here for future extension.
 * @param created True when thread is newly created, false when it's (about to be) destroyed.
 * @param user_data Calling application specified user data.
 */
typedef void (*rist_thread_callback_func_t)(void *handle, int type, bool created, void* user_data);

//Wrapper struct because ISO C forbids conversion from object pointer to function pointer
typedef struct {
	rist_thread_callback_func_t thread_callback;
} rist_thread_callback_t;

enum rist_opt
{
	//Set callback called when a thread is created or destroyed. This can only be set before rist_start is called.
	//optval1 must point to a rist_thread_callback_t struct, optval2 may contain a pointer to user data, optval3 must be NULL.
	RIST_OPT_THREAD_CALLBACK
};

/**
 * @brief Set option on RIST CTX
 * 
 * For usage see documentation for rist_opt enum
 */
RIST_API int rist_set_opt(struct rist_ctx *ctx, enum rist_opt opt, void* optval1, void* optval2, void* optval3);


#ifdef __cplusplus
}
#endif

#endif /* LIBRIST_OPT_H */
