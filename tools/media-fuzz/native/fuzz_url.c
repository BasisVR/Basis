/*
 * fuzz_url - libFuzzer target for the URL parser (basis_url_parse).
 *
 * The URL is the first attacker-controlled string the player touches: in
 * multiplayer a peer broadcasts it and every client parses it. basis_url_parse
 * splits scheme/userinfo/host/port/path by hand, so a malformed URL that walks
 * off the buffer or miscomputes a copy length faults here under ASan/UBSan
 * instead of on a user's machine.
 *
 * Build: see ../build.sh (clang -fsanitize=fuzzer,address,undefined).
 */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <stdlib.h>

#include "protocol/basis_url.h"

int LLVMFuzzerTestOneInput(const uint8_t* data, size_t size) {
    /* basis_url_parse wants a NUL-terminated C string; copy so a missing
     * terminator in the fuzz input can't itself read past the buffer. */
    char* s = (char*)malloc(size + 1);
    if (!s) return 0;
    memcpy(s, data, size);
    s[size] = 0;

    basis_url_t u;
    /* Read every out field on success so ASan validates the copies basis_url_parse
     * made (host/path/user/pass are fixed arrays it fills from the input). */
    if (basis_url_parse(s, &u) == 0) {
        volatile size_t sink = 0;
        sink ^= strlen(u.scheme) ^ strlen(u.host) ^ strlen(u.path)
              ^ strlen(u.user) ^ strlen(u.pass) ^ (size_t)u.port;
        (void)sink;
    }
    free(s);
    return 0;
}
