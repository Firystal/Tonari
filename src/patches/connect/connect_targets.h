#ifndef CHUNI_CONNECT_TARGETS_H
#define CHUNI_CONNECT_TARGETS_H
#include <stdint.h>

/* Host byte order; port is deliberately not used. The caller guard supplies
   the scope, and the native wrapper additionally requires SOCK_STREAM. */
static inline int cc_room_target_ip(uint32_t ip) {
    return ip >= 0xc0a88b0bU && ip <= 0xc0a88b0eU;
}
#endif
