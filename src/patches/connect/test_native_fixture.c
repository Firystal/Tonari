#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <windows.h>
#include <stdio.h>
#include <string.h>
#include <assert.h>
static unsigned room_stub_calls;
static int WSAAPI room_stub(SOCKET s,const struct sockaddr *a,int n) {
    (void)s;(void)a;(void)n; ++room_stub_calls;
    WSASetLastError(room_stub_calls==1?WSAEWOULDBLOCK:WSAEALREADY); return SOCKET_ERROR;
}
static void **find_connect(void) {
    unsigned char *base=(unsigned char *)GetModuleHandleW(NULL);
    IMAGE_DOS_HEADER *d=(IMAGE_DOS_HEADER *)base;
    IMAGE_NT_HEADERS32 *n=(IMAGE_NT_HEADERS32 *)(base+d->e_lfanew);
    IMAGE_IMPORT_DESCRIPTOR *desc=(IMAGE_IMPORT_DESCRIPTOR *)(base+n->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    for(;desc->Name;desc++) if(!_stricmp((char *)(base+desc->Name),"WS2_32.dll")) {
        IMAGE_THUNK_DATA32 *name=(IMAGE_THUNK_DATA32 *)(base+desc->OriginalFirstThunk),*iat=(IMAGE_THUNK_DATA32 *)(base+desc->FirstThunk);
        for(;name->u1.AddressOfData;name++,iat++) {
            if(IMAGE_SNAP_BY_ORDINAL32(name->u1.Ordinal)) { if(IMAGE_ORDINAL32(name->u1.Ordinal)==4) return (void **)&iat->u1.Function; }
            else if(!strcmp((char *)((IMAGE_IMPORT_BY_NAME *)(base+name->u1.AddressOfData))->Name,"connect")) return (void **)&iat->u1.Function;
        }
    }
    return NULL;
}
int main(int argc,char **argv) {
    assert(argc==4); const int refused=!strcmp(argv[3],"refused"), room_stub_case=!strcmp(argv[3],"production-room"), production=!strcmp(argv[3],"production") || room_stub_case;
    WSADATA data; assert(WSAStartup(MAKEWORD(2,2),&data)==0);
    SOCKET server=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP); assert(server!=INVALID_SOCKET);
    struct sockaddr_in address; memset(&address,0,sizeof(address)); address.sin_family=AF_INET; address.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
    assert(bind(server,(struct sockaddr *)&address,sizeof(address))==0); int size=sizeof(address); assert(getsockname(server,(struct sockaddr *)&address,&size)==0);
    if(refused) { assert(closesocket(server)==0); server=INVALID_SOCKET; } else assert(listen(server,4)==0);
    char port[16]; snprintf(port,sizeof(port),"%u",ntohs(address.sin_port)); SetEnvironmentVariableA("CC_TEST_PORT",port); SetEnvironmentVariableA("CHUNI_CONNECT_MODE",argv[2]);
    void **slot=find_connect(); assert(slot);
    if(room_stub_case) { DWORD old,ignored; assert(VirtualProtect(slot,sizeof(void *),PAGE_READWRITE,&old)); *slot=(void *)room_stub; assert(VirtualProtect(slot,sizeof(void *),old,&ignored)); }
    void *before=*slot;
    HMODULE dll=LoadLibraryA(argv[1]); if(!dll) { printf("LoadLibrary error=%lu\n",(unsigned long)GetLastError()); return 2; }
    assert(*slot!=before);
    SOCKET client=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP); assert(client!=INVALID_SOCKET); u_long nonblocking=1; assert(ioctlsocket(client,FIONBIO,&nonblocking)==0);
    if(room_stub_case) {
        /* The production DLL saves our preinstalled stub. This checks unknown
           build fallback for a room IP without sending a network packet. */
        address.sin_addr.s_addr=htonl(0xc0a88b0dU); address.sin_port=htons(55333);
        assert(connect(client,(struct sockaddr *)&address,sizeof(address))==SOCKET_ERROR && WSAGetLastError()==WSAEWOULDBLOCK);
        assert(connect(client,(struct sockaddr *)&address,sizeof(address))==SOCKET_ERROR && WSAGetLastError()==WSAEALREADY);
        assert(room_stub_calls==2); assert(closesocket(client)==0); assert(closesocket(server)==0);
        printf("PASS production unknown fixture pid=%lu mode=%s room=.13:55333 original_stub_calls=2 errors=10035,10037; no real room network calls\n",(unsigned long)GetCurrentProcessId(),argv[2]);
        Sleep(60); WSACleanup(); return 0;
    }
    int result=SOCKET_ERROR,error=0,calls=0; ULONGLONG start=GetTickCount64();
    do {
        result=connect(client,(struct sockaddr *)&address,sizeof(address)); error=WSAGetLastError(); ++calls;
        if(result==0 || error==WSAEISCONN || error==WSAECONNREFUSED) break;
        if(refused && !strcmp(argv[2],"observe")) {
            /* The observer must not repair repeated-connect semantics. The
               fixture is a correct Winsock client and consumes its own error. */
            fd_set writes,excepts; FD_ZERO(&writes); FD_ZERO(&excepts); FD_SET(client,&writes); FD_SET(client,&excepts);
            struct timeval wait={3,0}; int ready=select(0,NULL,&writes,&excepts,&wait); assert(ready>0 && FD_ISSET(client,&excepts));
            int so_error=0,so_size=sizeof(so_error); assert(getsockopt(client,SOL_SOCKET,SO_ERROR,(char *)&so_error,&so_size)==0);
            result=SOCKET_ERROR; error=so_error; break;
        }
        assert(error==WSAEWOULDBLOCK || error==WSAEALREADY || error==WSAEINVAL); Sleep(1);
    } while(GetTickCount64()-start<4000);
    if(refused) assert(result==SOCKET_ERROR && error==WSAECONNREFUSED);
    else assert(result==0 || error==WSAEISCONN);
    assert(closesocket(client)==0);
    if(server!=INVALID_SOCKET) assert(closesocket(server)==0);
    unsigned real_count=0,poll_count=0,error_count=0;
    if(!production) {
        typedef void (*counts_fn)(unsigned *,unsigned *,unsigned *); typedef int (*check_fn)(void);
        counts_fn counts=(counts_fn)GetProcAddress(dll,"CC_TestCounts"); check_fn check=(check_fn)GetProcAddress(dll,"CC_TestLastErrorAndClose"); check_fn targets=(check_fn)GetProcAddress(dll,"CC_TestTargetState"); assert(counts && check && targets);
        counts(&real_count,&poll_count,&error_count);
        if(!strcmp(argv[2],"compat")) { assert(real_count==1); if(calls>1) assert(poll_count>0); }
        else { assert(real_count==(unsigned)calls && poll_count==0 && error_count==0); }
        assert(check()==1);
        assert(targets()==1);
    }
    printf("PASS real x86 IAT pid=%lu mode=%s case=%s port=%s api_calls=%d real_calls=%u polls=%u SO_ERROR_reads=%u final_ret=%d final_wsa=%d; loopback only\n",(unsigned long)GetCurrentProcessId(),argv[2],argv[3],port,calls,real_count,poll_count,error_count,result,error);
    Sleep(60); WSACleanup(); return 0;
}
