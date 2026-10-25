#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include "connect_core.h"
#include "connect_targets.h"
#include "connect_signature.h"

typedef int (WSAAPI *connect_fn)(SOCKET, const struct sockaddr *, int);
typedef int (WSAAPI *close_fn)(SOCKET);
static connect_fn next_connect;
static close_fn next_close;
static int mode, requested_mode, shared_verified;
static HANDLE log_file = INVALID_HANDLE_VALUE;
static CRITICAL_SECTION state_lock, queue_lock;
static LARGE_INTEGER qpc_frequency;
static uintptr_t main_base;
static DWORD main_size;
static LONG serial;
#ifdef CC_TEST_NATIVE
static uint32_t target_ip = 0x7f000001U;
static uint16_t target_port;
#endif

enum { MAX_STATES = 256, QUEUE_SIZE = 512, LINE_SIZE = 768 };
typedef struct { SOCKET socket; LONG generation; int occupied, log_target, compat_owner, passthrough_only; struct sockaddr_in remote; cc_state state; uint64_t last_log_ms; unsigned calls, suppressed; int last_value, last_error; } entry;
static entry states[MAX_STATES];
static char queue[QUEUE_SIZE][LINE_SIZE];
static unsigned queue_read, queue_write, queue_count;
static LONG dropped_logs;
static char exit_buffer[QUEUE_SIZE * LINE_SIZE];
#ifdef CC_TEST_NATIVE
static unsigned test_real_calls, test_poll_calls, test_error_calls;
static int test_old_peer_ready;
#endif

static void enqueue(const char *line) {
    int saved = WSAGetLastError();
    EnterCriticalSection(&queue_lock);
    if (queue_count == QUEUE_SIZE) InterlockedIncrement(&dropped_logs);
    else {
        strncpy(queue[queue_write], line, LINE_SIZE - 1);
        queue[queue_write][LINE_SIZE - 1] = 0;
        queue_write = (queue_write + 1) % QUEUE_SIZE;
        ++queue_count;
    }
    LeaveCriticalSection(&queue_lock);
    WSASetLastError(saved);
}
static DWORD WINAPI writer(void *unused) {
    (void)unused;
    for (;;) {
        char line[LINE_SIZE]; int found = 0; DWORD written;
        EnterCriticalSection(&queue_lock);
        if (queue_count) {
            memcpy(line, queue[queue_read], LINE_SIZE);
            queue_read = (queue_read + 1) % QUEUE_SIZE; --queue_count; found = 1;
        }
        LeaveCriticalSection(&queue_lock);
        if (found) WriteFile(log_file, line, (DWORD)strlen(line), &written, NULL);
        else Sleep(20);
    }
}
static uint64_t ticks(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); return (uint64_t)t.QuadPart; }
static double micros(uint64_t delta) { return (double)delta * 1000000.0 / (double)qpc_frequency.QuadPart; }
static void log_call(const char *api, SOCKET socket, const char *stage,
                     uintptr_t caller, uint64_t begin, uint64_t end,
                     int value, int error, const struct sockaddr_in *remote,
                     const struct sockaddr_in *local, LONG generation,
                     uint64_t age_ms, unsigned calls, unsigned suppressed, const char *stack) {
    char line[LINE_SIZE]; SYSTEMTIME now; GetLocalTime(&now);
    const unsigned char *r = remote ? (const unsigned char *)&remote->sin_addr.s_addr : NULL;
    const unsigned char *l = local ? (const unsigned char *)&local->sin_addr.s_addr : NULL;
    unsigned rva = caller >= main_base && caller < main_base + main_size ? (unsigned)(caller-main_base) : 0;
    snprintf(line, sizeof(line),
        "%02u:%02u:%02u.%03u api=%s thread=%lu socket=0x%08lx generation=%ld mode=%s stage=%s caller=0x%08lx main_rva=0x%x qpc=%llu elapsed_us=%.3f result=%d wsa=%d local=%u.%u.%u.%u:%u remote=%u.%u.%u.%u:%u age_ms=%llu connect_calls=%u suppressed=%u dropped_logs=%ld stack=%s\r\n",
        now.wHour,now.wMinute,now.wSecond,now.wMilliseconds,api,(unsigned long)GetCurrentThreadId(),
        (unsigned long)socket,(long)generation,mode==CC_COMPAT?"compat":"observe",stage,(unsigned long)caller,rva,
        (unsigned long long)begin,micros(end-begin),value,error,
        l?l[0]:0,l?l[1]:0,l?l[2]:0,l?l[3]:0,local?ntohs(local->sin_port):0,
        r?r[0]:0,r?r[1]:0,r?r[2]:0,r?r[3]:0,remote?ntohs(remote->sin_port):0,
        (unsigned long long)age_ms,calls,suppressed,(long)dropped_logs,stack?stack:"-");
    enqueue(line);
}
static int copy_address(const struct sockaddr *address, int size, struct sockaddr_in *out) {
    SIZE_T copied = 0;
    return size >= (int)sizeof(*out) && address &&
        ReadProcessMemory(GetCurrentProcess(), address, out, sizeof(*out), &copied) &&
        copied == sizeof(*out) && out->sin_family == AF_INET;
}
static int is_target(const struct sockaddr_in *address) {
#ifdef CC_TEST_NATIVE
    return ntohl(address->sin_addr.s_addr) == target_ip && ntohs(address->sin_port) == target_port;
#else
    return cc_room_target_ip(ntohl(address->sin_addr.s_addr));
#endif
}
static int shared_call_allowed(uintptr_t caller) {
#ifdef CC_TEST_NATIVE
    (void)caller; return 1;
#else
    return cc_shared_caller(shared_verified,caller,main_base);
#endif
}
/* Entries are copied/committed under a short lock. Never hold the lock across
   Winsock calls, especially the caller's first potentially blocking connect. */
static entry snapshot(SOCKET socket) {
    entry result; memset(&result,0,sizeof(result)); result.socket=socket;
    EnterCriticalSection(&state_lock);
    for(int i=0;i<MAX_STATES;i++) {
        if(states[i].occupied && states[i].socket==socket) { result=states[i]; break; }
    }
    LeaveCriticalSection(&state_lock); return result;
}
static int same_remote(const struct sockaddr_in *a,const struct sockaddr_in *b) {
    return a->sin_family==b->sin_family && a->sin_addr.s_addr==b->sin_addr.s_addr && a->sin_port==b->sin_port;
}
/* A socket's pending state belongs to one exact destination. A changed target
   does not reset the kernel connection. Retain a passthrough-only guard until
   successful close, even across non-target calls, so readiness of the old peer
   can never be mistaken for a successful connection to the new peer. */
static entry snapshot_connect(SOCKET socket,const struct sockaddr_in *remote,int eligible,int compat_allowed) {
    entry result; memset(&result,0,sizeof(result)); result.socket=socket;
    EnterCriticalSection(&state_lock);
    int free_slot=-1;
    for(int i=0;i<MAX_STATES;i++) {
        if(states[i].occupied && states[i].socket==socket) {
            if(eligible && states[i].log_target && states[i].compat_owner==compat_allowed && same_remote(&states[i].remote,remote)) { result=states[i]; goto done; }
            result.occupied=1; result.generation=InterlockedIncrement(&serial);
            result.remote=*remote; result.log_target=eligible; result.compat_owner=compat_allowed; result.passthrough_only=1;
            states[i]=result; goto done;
        }
        if(!states[i].occupied && free_slot<0) free_slot=i;
    }
    if(eligible && free_slot>=0) {
        result.occupied=1; result.log_target=1; result.compat_owner=compat_allowed; result.generation=InterlockedIncrement(&serial); result.remote=*remote; states[free_slot]=result;
    }
done:
    LeaveCriticalSection(&state_lock); return result;
}
static void commit(const entry *value) {
    if(!value->occupied) return;
    EnterCriticalSection(&state_lock);
    for(int i=0;i<MAX_STATES;i++) if(states[i].occupied && states[i].socket==value->socket && states[i].generation==value->generation) {
        states[i]=*value; break;
    }
    LeaveCriticalSection(&state_lock);
}
static void erase(SOCKET socket, LONG generation) {
    EnterCriticalSection(&state_lock);
    for(int i=0;i<MAX_STATES;i++) if(states[i].occupied && states[i].socket==socket && states[i].generation==generation) {
        memset(&states[i],0,sizeof(states[i])); break;
    }
    LeaveCriticalSection(&state_lock);
}
typedef struct { SOCKET socket; const struct sockaddr *address; int size; } call_args;
static cc_result call_real(void *opaque) {
#ifdef CC_TEST_NATIVE
    ++test_real_calls;
#endif
    call_args *a=(call_args *)opaque;
    int result=next_connect(a->socket,a->address,a->size);
    int error=WSAGetLastError();
    return cc_make(result,error,"passthrough");
}
static int call_poll(void *opaque,int *error) {
#ifdef CC_TEST_NATIVE
    ++test_poll_calls;
    if(test_old_peer_ready) return CC_WRITE_READY;
#endif
    call_args *a=(call_args *)opaque; fd_set write_set, except_set; struct timeval timeout={0,0};
    FD_ZERO(&write_set); FD_ZERO(&except_set); FD_SET(a->socket,&write_set); FD_SET(a->socket,&except_set);
    int n=select(0,NULL,&write_set,&except_set,&timeout);
    if(n==SOCKET_ERROR) { *error=WSAGetLastError(); return -1; }
    return (FD_ISSET(a->socket,&write_set)?CC_WRITE_READY:0)|(FD_ISSET(a->socket,&except_set)?CC_EXCEPTION:0);
}
static int call_error(void *opaque,int *value,int *error) {
#ifdef CC_TEST_NATIVE
    ++test_error_calls;
    if(test_old_peer_ready) { *value=0; return 0; }
#endif
    call_args *a=(call_args *)opaque; int size=sizeof(*value);
    int result=getsockopt(a->socket,SOL_SOCKET,SO_ERROR,(char *)value,&size);
    if(result==SOCKET_ERROR) *error=WSAGetLastError(); return result;
}
static int WSAAPI hooked_connect(SOCKET socket,const struct sockaddr *address,int size) {
    int incoming=WSAGetLastError(); uintptr_t caller=(uintptr_t)__builtin_return_address(0); uint64_t begin=ticks();
    struct sockaddr_in remote,local; memset(&remote,0,sizeof(remote)); memset(&local,0,sizeof(local));
    int valid=copy_address(address,size,&remote), type=0, type_size=sizeof(type);
    int eligible=valid && is_target(&remote) && getsockopt(socket,SOL_SOCKET,SO_TYPE,(char *)&type,&type_size)==0 && type==SOCK_STREAM;
    int compat_allowed=eligible && shared_call_allowed(caller);
    entry record=snapshot_connect(socket,&remote,eligible,compat_allowed); call_args a={socket,address,size}; cc_ops ops={&a,call_real,call_poll,call_error};
    uint64_t call_ms=GetTickCount64();
    WSASetLastError(incoming);
    cc_result result=cc_connect(&record.state,compat_allowed?mode:CC_OBSERVE,eligible && record.occupied && !record.passthrough_only,call_ms,&ops);
    if(eligible && record.passthrough_only) result.stage="retarget-passthrough-until-close";
    else if(eligible && mode==CC_COMPAT && !compat_allowed) result.stage="outside-shared-caller-observe";
    uint64_t end=ticks(),now_ms=GetTickCount64();
    /* Only our exact destination is logged. Non-target behavior passes through. */
    if(eligible) {
        ++record.calls;
        int show=record.calls==1 || result.value!=record.last_value || result.error!=record.last_error || now_ms-record.last_log_ms>=1000;
        if(show) {
            int local_size=sizeof(local); if(getsockname(socket,(struct sockaddr *)&local,&local_size)!=0) memset(&local,0,sizeof(local));
            log_call("connect",socket,record.occupied?result.stage:"capacity-passthrough",caller,begin,end,result.value,result.error,&remote,&local,record.generation,
                record.state.since_ms?now_ms-record.state.since_ms:0,record.calls,record.suppressed,NULL);
            record.last_log_ms=now_ms; record.suppressed=0;
        } else ++record.suppressed;
        record.last_value=result.value; record.last_error=result.error;
    }
    commit(&record);
    WSASetLastError(result.error); return result.value;
}
static int WSAAPI hooked_close(SOCKET socket) {
    int incoming=WSAGetLastError(); uintptr_t caller=(uintptr_t)__builtin_return_address(0); uint64_t begin=ticks();
    entry record=snapshot(socket); struct sockaddr_in local,remote; memset(&local,0,sizeof(local)); memset(&remote,0,sizeof(remote));
    if(record.occupied) { int length=sizeof(local); if(getsockname(socket,(struct sockaddr *)&local,&length)!=0) memset(&local,0,sizeof(local)); remote=record.remote; }
    WSASetLastError(incoming); int result=next_close(socket); int error=WSAGetLastError(); uint64_t end=ticks();
    /* A failed close has not released the handle. Preserve its record. */
    if(result==0 && record.occupied) erase(socket,record.generation);
    if(record.occupied && record.log_target) {
        void *frames[6]; char stack[96]; stack[0]=0;
        USHORT n=CaptureStackBackTrace(1,6,frames,NULL); size_t used=0;
        for(USHORT i=0;i<n && used<sizeof(stack);i++) used+=(size_t)snprintf(stack+used,sizeof(stack)-used,i?",0x%08lx":"0x%08lx",(unsigned long)(uintptr_t)frames[i]);
        log_call("closesocket",socket,result==0?"close":"close-error",caller,begin,end,result,error,&remote,&local,record.generation,
            record.state.since_ms?GetTickCount64()-record.state.since_ms:0,record.calls,record.suppressed,stack);
    }
    WSASetLastError(error); return result;
}

static int write_pointer(void **slot,void *value) {
    DWORD old,ignored;
    if(!VirtualProtect(slot,sizeof(void *),PAGE_READWRITE,&old)) return 0;
    InterlockedExchangePointer((PVOID volatile *)slot,value);
    if(!VirtualProtect(slot,sizeof(void *),old,&ignored)) return 0;
    return 1;
}
static int locate_imports(void ***connect_slot,void ***close_slot,void ***ioctl_slot,void ***error_slot,int *guard_ambiguous) {
    unsigned char *base=(unsigned char *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *dos=(IMAGE_DOS_HEADER *)base;
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE) return 0;
    IMAGE_NT_HEADERS32 *nt=(IMAGE_NT_HEADERS32 *)(base+dos->e_lfanew);
    if(nt->Signature!=IMAGE_NT_SIGNATURE || nt->FileHeader.Machine!=IMAGE_FILE_MACHINE_I386 || nt->OptionalHeader.Magic!=IMAGE_NT_OPTIONAL_HDR32_MAGIC) return 0;
    main_base=(uintptr_t)base; main_size=nt->OptionalHeader.SizeOfImage;
    DWORD rva=nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress;
    if(!rva || rva>=main_size) return 0;
    IMAGE_IMPORT_DESCRIPTOR *desc=(IMAGE_IMPORT_DESCRIPTOR *)(base+rva);
    for(;desc->Name;desc++) {
        if(_stricmp((char *)(base+desc->Name),"ws2_32.dll")!=0) continue;
        if(!desc->OriginalFirstThunk || !desc->FirstThunk) return 0;
        IMAGE_THUNK_DATA32 *names=(IMAGE_THUNK_DATA32 *)(base+desc->OriginalFirstThunk);
        IMAGE_THUNK_DATA32 *iat=(IMAGE_THUNK_DATA32 *)(base+desc->FirstThunk);
        for(;names->u1.AddressOfData;names++,iat++) {
            int kind=0;
            if(IMAGE_SNAP_BY_ORDINAL32(names->u1.Ordinal)) {
                WORD ordinal=IMAGE_ORDINAL32(names->u1.Ordinal); if(ordinal==4) kind=1; if(ordinal==3) kind=2; if(ordinal==10) kind=3; if(ordinal==111) kind=4;
            } else {
                const char *name=(const char *)((IMAGE_IMPORT_BY_NAME *)(base+names->u1.AddressOfData))->Name;
                if(!strcmp(name,"connect")) kind=1; if(!strcmp(name,"closesocket")) kind=2; if(!strcmp(name,"ioctlsocket")) kind=3; if(!strcmp(name,"WSAGetLastError")) kind=4;
            }
            if(kind==1) { if(*connect_slot) return 0; *connect_slot=(void **)&iat->u1.Function; }
            if(kind==2) { if(*close_slot) return 0; *close_slot=(void **)&iat->u1.Function; }
            if(kind==3) { if(*ioctl_slot) *guard_ambiguous=1; *ioctl_slot=(void **)&iat->u1.Function; }
            if(kind==4) { if(*error_slot) *guard_ambiguous=1; *error_slot=(void **)&iat->u1.Function; }
        }
    }
    return *connect_slot && *close_slot;
}
#ifndef CC_TEST_NATIVE
static int verify_shared_code(void **connect_slot,void **ioctl_slot,void **error_slot,int ambiguous) {
    if(ambiguous || (uintptr_t)ioctl_slot!=main_base+0x1e1abf4U ||
       (uintptr_t)error_slot!=main_base+0x1e1ac38U || main_size<CC_SIGNATURE_RVA+CC_SIGNATURE_LENGTH) return 0;
    const void *address=(const void *)(main_base+CC_SIGNATURE_RVA); MEMORY_BASIC_INFORMATION region;
    if(!VirtualQuery(address,&region,sizeof(region)) || region.State!=MEM_COMMIT ||
       (region.Protect&(PAGE_GUARD|PAGE_NOACCESS)) ||
       !(region.Protect&(PAGE_EXECUTE|PAGE_EXECUTE_READ|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)) ||
       (uintptr_t)address-(uintptr_t)region.BaseAddress+CC_SIGNATURE_LENGTH>region.RegionSize) return 0;
    unsigned char code[CC_SIGNATURE_LENGTH]; SIZE_T copied=0;
    if(!ReadProcessMemory(GetCurrentProcess(),address,code,sizeof(code),&copied) || copied!=sizeof(code)) return 0;
    return cc_shared_signature_matches(code,sizeof(code),main_base,(uintptr_t)connect_slot);
}
#endif
static void install(HINSTANCE self) {
    wchar_t setting[32],exe_path[MAX_PATH],dll_path[MAX_PATH],log_path[MAX_PATH];
    DWORD count=GetEnvironmentVariableW(L"CHUNI_CONNECT_MODE",setting,32);
    if(count>=32) return;
    if(!count || !_wcsicmp(setting,L"observe")) mode=CC_OBSERVE;
    else if(!_wcsicmp(setting,L"compat")) mode=CC_COMPAT;
    else return;
    requested_mode=mode;
#ifdef CC_TEST_NATIVE
    wchar_t port_text[16]; DWORD pn=GetEnvironmentVariableW(L"CC_TEST_PORT",port_text,16);
    if(!pn || pn>=16) return; unsigned long port=wcstoul(port_text,NULL,10);
    if(port<1 || port>65535) return; target_ip=0x7f000001U; target_port=(uint16_t)port;
#endif
    DWORD length=GetModuleFileNameW(NULL,exe_path,MAX_PATH);
    if(!length || length>=MAX_PATH) return;
    wchar_t *name=wcsrchr(exe_path,L'\\'); name=name?name+1:exe_path;
    if(_wcsicmp(name,L"chusanApp.exe")) return;
    void **connect_slot=NULL,**close_slot=NULL,**ioctl_slot=NULL,**error_slot=NULL; int guard_ambiguous=0;
    if(!locate_imports(&connect_slot,&close_slot,&ioctl_slot,&error_slot,&guard_ambiguous)) return;
#ifdef CC_TEST_NATIVE
    shared_verified=1; /* Test-only loopback build, never shipped as production. */
#else
    shared_verified=verify_shared_code(connect_slot,ioctl_slot,error_slot,guard_ambiguous);
    if(!shared_verified) mode=CC_OBSERVE;
#endif
    length=GetModuleFileNameW(self,dll_path,MAX_PATH);
    if(!length || length>=MAX_PATH) return;
    wchar_t *slash=wcsrchr(dll_path,L'\\'); if(!slash) return; *slash=0;
    SYSTEMTIME now; GetLocalTime(&now);
    int written=swprintf(log_path,MAX_PATH,L"%ls\\connect-%04u%02u%02u-%02u%02u%02u-pid%lu.log",dll_path,now.wYear,now.wMonth,now.wDay,now.wHour,now.wMinute,now.wSecond,(unsigned long)GetCurrentProcessId());
    if(written<0 || written>=MAX_PATH) return;
    log_file=CreateFileW(log_path,GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,NULL);
    if(log_file==INVALID_HANDLE_VALUE) return;
    InitializeCriticalSection(&state_lock); InitializeCriticalSection(&queue_lock); QueryPerformanceFrequency(&qpc_frequency);
    HMODULE pinned=NULL;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(LPCWSTR)&install,&pinned)) { CloseHandle(log_file); log_file=INVALID_HANDLE_VALUE; return; }
    HANDLE thread=CreateThread(NULL,0,writer,NULL,0,NULL);
    if(!thread) { CloseHandle(log_file); log_file=INVALID_HANDLE_VALUE; return; } CloseHandle(thread);
    next_connect=(connect_fn)*connect_slot; next_close=(close_fn)*close_slot;
    char intro[LINE_SIZE];
#ifdef CC_TEST_NATIVE
    char targets[96]; snprintf(targets,sizeof(targets),"TEST LOOPBACK ONLY 127.0.0.1:%u/TCP",target_port);
#else
    const char *targets="verified shared caller + 192.168.139.11-192.168.139.14 + TCP; any port";
#endif
    snprintf(intro,sizeof(intro),"CHUNI connect diagnostic v2-shared; requested=%s; mode=%s; main-IAT-only; targets=%s; shared_signature=%s caller_rva=0x%x; timeout=3000ms; no payload logging; close always forwarded; executable file unchanged; base=0x%08lx size=%lu; next_connect=0x%08lx next_close=0x%08lx; qpc_frequency=%llu\r\n",requested_mode==CC_COMPAT?"compat":"observe",mode==CC_COMPAT?"compat":"observe",targets,shared_verified?"verified":"MISMATCH",CC_SHARED_RETURN_RVA,(unsigned long)main_base,(unsigned long)main_size,(unsigned long)(uintptr_t)next_connect,(unsigned long)(uintptr_t)next_close,(unsigned long long)qpc_frequency.QuadPart); enqueue(intro);
    if(!shared_verified) enqueue("COMPAT DISABLED: shared callsite signature/import slot mismatch. Observe-only for room TCP; every original connect result is forwarded unchanged. No address guessing or environment override.\r\n");
#ifdef CC_TEST_NATIVE
    enqueue("TEST BUILD ONLY: loopback target and caller/signature bypass for independent fixture validation. NOT FOR GAME USE.\r\n");
#endif
    if(!write_pointer(connect_slot,(void *)hooked_connect) || !write_pointer(close_slot,(void *)hooked_close)) {
        write_pointer(connect_slot,(void *)next_connect); write_pointer(close_slot,(void *)next_close);
        enqueue("INSTALL FAILED; rollback attempted; do not use this run as diagnostic evidence.\r\n"); return;
    }
    enqueue("INSTALLED connect/closesocket. Imported WSAConnect/ConnectEx and other module IATs are outside coverage.\r\n");
}
BOOL WINAPI DllMain(HINSTANCE instance,DWORD reason,LPVOID reserved) {
    if(reason==DLL_PROCESS_ATTACH) { DisableThreadLibraryCalls(instance); install(instance); }
    /* On process termination the other threads are already gone. Never wait
       for the worker/locks under loader lock; best-effort single local write. */
    else if(reason==DLL_PROCESS_DETACH && reserved && log_file!=INVALID_HANDLE_VALUE && TryEnterCriticalSection(&queue_lock)) {
        DWORD written; size_t used=0;
        while(queue_count) { size_t n=strlen(queue[queue_read]); memcpy(exit_buffer+used,queue[queue_read],n); used+=n; queue_read=(queue_read+1)%QUEUE_SIZE; --queue_count; }
        if(used) WriteFile(log_file,exit_buffer,(DWORD)used,&written,NULL);
        CloseHandle(log_file); log_file=INVALID_HANDLE_VALUE; LeaveCriticalSection(&queue_lock);
    }
    return TRUE;
}
#ifdef CC_TEST_NATIVE
__declspec(dllexport) void CC_TestCounts(unsigned *real_count,unsigned *poll_count,unsigned *error_count) {
    *real_count=test_real_calls; *poll_count=test_poll_calls; *error_count=test_error_calls;
}
static unsigned test_close_calls;
static int test_close_value;
static unsigned test_pending_calls;
static int test_pending_error;
static int WSAAPI fake_connect_pending(SOCKET s,const struct sockaddr *a,int n) { (void)s;(void)a;(void)n; ++test_pending_calls; WSASetLastError(test_pending_error); return SOCKET_ERROR; }
static int WSAAPI fake_connect_error(SOCKET s,const struct sockaddr *a,int n) { (void)s;(void)a;(void)n; WSASetLastError(10022); return SOCKET_ERROR; }
static int WSAAPI fake_close_error(SOCKET s) { (void)s; ++test_close_calls; WSASetLastError(test_close_value==0?6500:10035); return test_close_value; }
__declspec(dllexport) int CC_TestLastErrorAndClose(void) {
    SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP); if(s==INVALID_SOCKET) return 0;
    struct sockaddr_in a; memset(&a,0,sizeof(a)); a.sin_family=AF_INET; a.sin_addr.s_addr=htonl(target_ip); a.sin_port=htons(target_port);
    connect_fn saved_connect=next_connect; close_fn saved_close=next_close; int okay=1;
    next_connect=fake_connect_error; next_close=fake_close_error; test_close_calls=0;
    WSASetLastError(2345); int result=hooked_connect(s,(struct sockaddr *)&a,sizeof(a)); int error=WSAGetLastError();
    if(result!=SOCKET_ERROR || error!=10022) okay=0;
    test_close_value=SOCKET_ERROR; result=hooked_close(s); error=WSAGetLastError();
    if(result!=SOCKET_ERROR || error!=10035 || !snapshot(s).occupied) okay=0;
    test_close_value=0; result=hooked_close(s); error=WSAGetLastError();
    if(result!=0 || error!=6500 || snapshot(s).occupied || test_close_calls!=2) okay=0;
    next_connect=saved_connect; next_close=saved_close; saved_close(s); return okay;
}
__declspec(dllexport) int CC_TestTargetState(void) {
    SOCKET s=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP), other=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);
    if(s==INVALID_SOCKET || other==INVALID_SOCKET) return 0;
    connect_fn saved_connect=next_connect; close_fn saved_close=next_close;
    uint16_t saved_port=target_port; uint32_t saved_ip=target_ip; int saved_mode=mode,okay=1;
    unsigned previous_polls=test_poll_calls,previous_errors=test_error_calls; test_pending_calls=0; test_pending_error=10035; mode=CC_COMPAT;
    next_connect=fake_connect_pending; next_close=fake_close_error; test_close_value=0;
    struct sockaddr_in a,b,outside; memset(&a,0,sizeof(a)); a.sin_family=AF_INET;
    a.sin_addr.s_addr=htonl(target_ip); a.sin_port=htons(target_port); b=a; outside=a;
    if(hooked_connect(s,(struct sockaddr *)&a,sizeof(a))!=-1 || WSAGetLastError()!=10035) okay=0;
    entry first=snapshot(s); if(!first.occupied || first.state.phase!=CC_PENDING || !same_remote(&first.remote,&a)) okay=0;
    if(hooked_connect(other,(struct sockaddr *)&a,sizeof(a))!=-1 || WSAGetLastError()!=10035) okay=0;
    entry separate=snapshot(other); if(!separate.occupied || separate.generation==first.generation) okay=0;
    target_port=(uint16_t)(saved_port==65535?65534:saved_port+1); b.sin_port=htons(target_port);
    /* Simulate the old peer already writable/SO_ERROR=0 while a new connect
       returns ALREADY. Any reuse of old readiness would fabricate success. */
    test_old_peer_ready=1; test_pending_error=10037;
    if(hooked_connect(s,(struct sockaddr *)&b,sizeof(b))!=-1 || WSAGetLastError()!=10037) okay=0;
    entry second=snapshot(s);
    if(!second.occupied || !second.passthrough_only || second.generation==first.generation || !same_remote(&second.remote,&b) || second.calls!=1 || test_pending_calls!=3 || test_poll_calls!=previous_polls) okay=0;
    if(hooked_connect(s,(struct sockaddr *)&b,sizeof(b))!=-1 || WSAGetLastError()!=10037 || test_pending_calls!=4) okay=0;
    first.state.phase=CC_EXPIRED; commit(&first); entry after=snapshot(s);
    if(after.generation!=second.generation || after.state.phase!=CC_EMPTY || !after.passthrough_only || !same_remote(&after.remote,&b)) okay=0;
    entry other_after=snapshot(other); if(other_after.generation!=separate.generation || !same_remote(&other_after.remote,&a)) okay=0;
    /* Unlisted calls pass through while preserving the guard until close. */
    outside.sin_addr.s_addr=htonl(0x0a010101U);
    if(hooked_connect(s,(struct sockaddr *)&outside,sizeof(outside))!=-1 || WSAGetLastError()!=10037 || !snapshot(s).passthrough_only || snapshot(s).log_target || test_pending_calls!=5) okay=0;
    if(hooked_connect(s,(struct sockaddr *)&b,sizeof(b))!=-1 || WSAGetLastError()!=10037 || test_pending_calls!=6) okay=0;
    entry third=snapshot(s); if(third.generation==second.generation || !third.passthrough_only || third.calls!=1 || !same_remote(&third.remote,&b)) okay=0;
    target_ip=0x7f000002U; b.sin_addr.s_addr=htonl(target_ip);
    if(hooked_connect(s,(struct sockaddr *)&b,sizeof(b))!=-1 || WSAGetLastError()!=10037 || test_pending_calls!=7) okay=0;
    if(hooked_connect(s,(struct sockaddr *)&b,sizeof(b))!=-1 || WSAGetLastError()!=10037 || test_pending_calls!=8) okay=0;
    /* Close logs each saved destination; the fake close only checks forwarding. */
    if(hooked_close(s)!=0 || WSAGetLastError()!=6500 || snapshot(s).occupied) okay=0;
    if(hooked_close(other)!=0 || WSAGetLastError()!=6500 || snapshot(other).occupied) okay=0;
    if(test_poll_calls!=previous_polls || test_error_calls!=previous_errors) okay=0;
    next_connect=saved_connect; next_close=saved_close; target_port=saved_port; target_ip=saved_ip; mode=saved_mode; test_old_peer_ready=0;
    saved_close(s); saved_close(other); return okay;
}
#endif
