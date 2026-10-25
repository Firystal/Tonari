#ifndef CHUNI_CONNECT_CORE_H
#define CHUNI_CONNECT_CORE_H
#include <stdint.h>

enum { CC_OBSERVE = 0, CC_COMPAT = 1 };
enum { CC_EMPTY = 0, CC_PENDING = 1, CC_EXPIRED = 2 };
enum { CC_WOULDBLOCK = 10035, CC_ALREADY = 10037, CC_TIMEDOUT = 10060 };
enum { CC_WRITE_READY = 1, CC_EXCEPTION = 2 };
typedef struct { int phase; uint64_t since_ms; } cc_state;
typedef struct { int value; int error; const char *stage; } cc_result;
typedef struct {
    void *user;
    cc_result (*real_connect)(void *);
    /* A negative return is select failure; error is then the actual WSA error. */
    int (*poll)(void *, int *error);
    /* Returns zero on success and writes the actual SO_ERROR value. */
    int (*socket_error)(void *, int *value, int *error);
} cc_ops;

static cc_result cc_make(int value, int error, const char *stage) {
    cc_result r = { value, error, stage }; return r;
}
/* Pure decision layer. It never closes a socket or retries the real connect
   while its own pending record exists. No background or infinite wait. */
static cc_result cc_connect(cc_state *s, int mode, int eligible,
                            uint64_t now_ms, const cc_ops *ops) {
    if (!eligible) return ops->real_connect(ops->user);
    if (mode == CC_COMPAT && s->phase == CC_EXPIRED)
        return cc_make(-1, CC_TIMEDOUT, "timeout-sticky");
    if (mode == CC_COMPAT && s->phase == CC_PENDING) {
        int error = 0, so_error = 0;
        int ready = ops->poll(ops->user, &error);
        if (ready < 0) { s->phase = CC_EMPTY; return cc_make(-1, error, "poll-error"); }
        if (ready != 0) {
            if (ops->socket_error(ops->user, &so_error, &error) != 0) {
                s->phase = CC_EMPTY; return cc_make(-1, error, "getsockopt-error");
            }
            if (so_error != 0) { s->phase = CC_EMPTY; return cc_make(-1, so_error, "connect-error"); }
            if ((ready & CC_WRITE_READY) != 0) {
                s->phase = CC_EMPTY; return cc_make(0, 0, "connected");
            }
            /* An exceptional notification with SO_ERROR=0 is not success. */
        }
        if (now_ms - s->since_ms >= 3000) {
            s->phase = CC_EXPIRED; return cc_make(-1, CC_TIMEDOUT, "timeout");
        }
        return cc_make(-1, CC_WOULDBLOCK, "pending");
    }
    cc_result r = ops->real_connect(ops->user);
    if (r.value == -1 && (r.error == CC_WOULDBLOCK || r.error == CC_ALREADY)) {
        int existed = s->phase == CC_PENDING;
        if (!existed) s->since_ms = now_ms;
        s->phase = CC_PENDING;
        r.stage = existed ? "real-pending-repeat" : "real-pending-first";
    } else {
        s->phase = CC_EMPTY;
        r.stage = r.value == 0 ? "real-connected" : "real-error";
    }
    return r;
}
#endif
