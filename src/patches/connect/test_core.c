#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "connect_core.h"
#include "connect_targets.h"
typedef struct { int real_calls,poll_calls,error_calls; cc_result real_result; int ready,poll_error,so_error,get_error; } fake;
static cc_result real(void *p) { fake *f=p; f->real_calls++; return f->real_result; }
static int poll(void *p,int *error) { fake *f=p; f->poll_calls++; *error=f->poll_error; return f->ready; }
static int socket_error(void *p,int *value,int *error) { fake *f=p; f->error_calls++; *value=f->so_error; *error=f->get_error; return f->get_error?-1:0; }
static void reset(fake *f,cc_state *s) { memset(f,0,sizeof(*f)); memset(s,0,sizeof(*s)); f->real_result=cc_make(-1,10035,"real"); }
int main(void) {
    fake f; cc_state s; cc_ops ops={&f,real,poll,socket_error}; cc_result r; unsigned checks=0;
    #define CHECK(x) do { assert(x); ++checks; } while(0)
    reset(&f,&s); r=cc_connect(&s,CC_COMPAT,1,100,&ops);
    CHECK(r.value==-1 && r.error==10035 && s.phase==CC_PENDING && f.real_calls==1);
    r=cc_connect(&s,CC_COMPAT,1,101,&ops); CHECK(r.error==10035 && f.real_calls==1 && f.poll_calls==1 && f.error_calls==0);
    f.ready=CC_WRITE_READY; r=cc_connect(&s,CC_COMPAT,1,150,&ops); CHECK(r.value==0 && f.real_calls==1 && f.error_calls==1 && s.phase==CC_EMPTY);
    reset(&f,&s); f.real_result=cc_make(-1,10061,"real"); r=cc_connect(&s,CC_COMPAT,1,0,&ops); CHECK(r.value==-1 && r.error==10061 && f.poll_calls==0 && s.phase==CC_EMPTY);
    reset(&f,&s); cc_connect(&s,CC_COMPAT,1,0,&ops); f.ready=CC_EXCEPTION; f.so_error=10061;
    r=cc_connect(&s,CC_COMPAT,1,30,&ops); CHECK(r.value==-1 && r.error==10061 && f.real_calls==1);
    reset(&f,&s); cc_connect(&s,CC_COMPAT,1,0,&ops); f.ready=CC_EXCEPTION;
    r=cc_connect(&s,CC_COMPAT,1,30,&ops); CHECK(r.value==-1 && r.error==10035 && s.phase==CC_PENDING);
    r=cc_connect(&s,CC_COMPAT,1,3000,&ops); CHECK(r.value==-1 && r.error==10060 && s.phase==CC_EXPIRED);
    int count=f.poll_calls; r=cc_connect(&s,CC_COMPAT,1,9000,&ops); CHECK(r.error==10060 && f.poll_calls==count && f.real_calls==1);
    reset(&f,&s); cc_connect(&s,CC_COMPAT,1,0,&ops); f.ready=CC_WRITE_READY;
    r=cc_connect(&s,CC_COMPAT,1,3100,&ops); CHECK(r.value==0); /* Completion wins over checking the deadline. */
    reset(&f,&s); r=cc_connect(&s,CC_OBSERVE,1,0,&ops); f.real_result=cc_make(-1,10037,"real");
    r=cc_connect(&s,CC_OBSERVE,1,1,&ops); CHECK(r.error==10037 && f.real_calls==2 && f.poll_calls==0 && f.error_calls==0);
    reset(&f,&s); s.phase=CC_PENDING; f.real_result=cc_make(42,12345,"real"); r=cc_connect(&s,CC_COMPAT,0,9999,&ops);
    CHECK(r.value==42 && r.error==12345 && f.real_calls==1 && f.poll_calls==0 && f.error_calls==0);
    reset(&f,&s); cc_connect(&s,CC_COMPAT,1,0,&ops); f.ready=-1; f.poll_error=10038;
    r=cc_connect(&s,CC_COMPAT,1,1,&ops); CHECK(r.error==10038 && f.error_calls==0 && s.phase==CC_EMPTY);
    reset(&f,&s); cc_connect(&s,CC_COMPAT,1,0,&ops); f.ready=CC_WRITE_READY; f.get_error=10038;
    r=cc_connect(&s,CC_COMPAT,1,1,&ops); CHECK(r.error==10038 && r.value==-1);
    reset(&f,&s); f.real_result=cc_make(-1,10037,"real"); r=cc_connect(&s,CC_COMPAT,1,0,&ops); CHECK(s.phase==CC_PENDING && r.error==10037);
    reset(&f,&s); f.real_result=cc_make(-1,10022,"real"); r=cc_connect(&s,CC_COMPAT,1,0,&ops); CHECK(s.phase==CC_EMPTY && r.error==10022);
    reset(&f,&s); f.real_result=cc_make(0,777,"real"); r=cc_connect(&s,CC_OBSERVE,1,0,&ops); CHECK(r.value==0 && r.error==777);
    for(uint32_t ip=0xc0a88b0bU;ip<=0xc0a88b0eU;ip++) {
        CHECK(cc_room_target_ip(ip));
    }
    CHECK(!cc_room_target_ip(0xc0a88b0aU)); CHECK(!cc_room_target_ip(0xc0a88b0fU));
    CHECK(!cc_room_target_ip(0x7f000001U)); CHECK(!cc_room_target_ip(0xc0a88affU));
    printf("PASS %u decision checks. No game started and no network access.\n",checks);
    return 0;
}
