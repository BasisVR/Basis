/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef RIST_COMMON_H
#define RIST_COMMON_H

#if defined _WIN32
    #if defined LIBRIST_BUILDING_DLL
    #define RIST_API __declspec(dllexport)
    #else
    #define RIST_API
    #endif
#else
    #if __GNUC__ >= 4
    #define RIST_API __attribute__ ((visibility ("default")))
    #else
    #define RIST_API
    #endif
#endif

#if defined(__GNUC__)
	#define RIST_DEPRECATED __attribute__((deprecated))
#elif defined(_MSC_VER)
	#define RIST_DEPRECATED __declspec(deprecated)
#else
	#define RIST_DEPRECATED
#endif

#endif /* RIST_COMMON_H */
