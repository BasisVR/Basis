/*
 * Copyright © 2020, VideoLAN and librist authors
 * Copyright © 2019-2020 SipRadius LLC
 * All rights reserved.
 *
 * SPDX-License-Identifier: BSD-2-Clause
 */

#ifndef LIBRIST_VERSION_H
#define LIBRIST_VERSION_H

#define LIBRIST_API_VERSION_MAJOR 4
#define LIBRIST_API_VERSION_MINOR 5
#define LIBRIST_API_VERSION_PATCH 0

#define LIBRIST_API_VERSION 0x40500

//Helper macro to check against LIBRIST_API_VERSION
#define LIBRIST_MAKE_API_VERSION(major, minor, patch) \
    ((patch) + ((minor) *0x100) + ((major) *0x10000))

#endif /* LIBRIST_VERSION_H */
