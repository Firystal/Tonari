#define WIN32_LEAN_AND_MEAN
#define _WIN32_WINNT 0x0601
#include <winsock2.h>
#include <windows.h>
#include <stdint.h>
#include <stdio.h>
#include <stdarg.h>
#include <wchar.h>
#include <limits.h>
#include "sha256_local.h"
#include "registry_signature.h"

/* Load only via the existing pre-entry injection launcher. No remote attach,
   no IAT writes, no fabricated registry success or property values. */
typedef LSTATUS (WINAPI *regopen_fn)(HKEY,LPCWSTR,DWORD,REGSAM,PHKEY);
static uintptr_t main_base,hook_base;
static regopen_fn expected_hook;
static HANDLE logfile=INVALID_HANDLE_VALUE;
static CRITICAL_SECTION log_lock;
static unsigned char *thunk;
static LONG mismatch_logged;
static const uint32_t call_rva=0x301b0b,iat_rva=0x5b10d8;
static const unsigned char original_call[6]={0xff,0x15,0xc7,0xf5,0x2a,0};
static const wchar_t target_key[]=L"System\\Sega\\SystemProperty\\keychip";
enum { RETRY_BUDGET_MS=2000, MAX_ATTEMPTS=2000 };
#ifdef REGFIX_TEST
static ULONGLONG test_clock;
static unsigned test_sleep_count,test_sleep_step=1;
static int test_fail_flush;
static regopen_fn test_sleep_iat;
#endif
static ULONGLONG tick_now(void){
#ifdef REGFIX_TEST
    return test_clock;
#else
    return GetTickCount64();
#endif
}
static void sleep_one(void){
#ifdef REGFIX_TEST
    ++test_sleep_count;test_clock+=test_sleep_step;
    if(test_sleep_iat)*(regopen_fn *)(main_base+iat_rva)=test_sleep_iat;
    SetLastError(9999);
#else
    Sleep(1);
#endif
}
static void restore_errors(DWORD win,int wsa){WSASetLastError(wsa);SetLastError(win);}
static void log_line(const char *fmt,...){
    DWORD win=GetLastError();int wsa=WSAGetLastError();
    if(logfile!=INVALID_HANDLE_VALUE){char line[1024];SYSTEMTIME st;GetLocalTime(&st);
        int n=snprintf(line,sizeof(line),"%04u-%02u-%02u %02u:%02u:%02u.%03u tid=%lu ",st.wYear,st.wMonth,st.wDay,st.wHour,st.wMinute,st.wSecond,st.wMilliseconds,(unsigned long)GetCurrentThreadId());
        va_list args;va_start(args,fmt);vsnprintf(line+n,sizeof(line)-(size_t)n-3,fmt,args);va_end(args);
        size_t len=strlen(line);line[len++]='\r';line[len++]='\n';DWORD done;
        EnterCriticalSection(&log_lock);WriteFile(logfile,line,(DWORD)len,&done,NULL);LeaveCriticalSection(&log_lock);
    }restore_errors(win,wsa);
}
static regopen_fn current_open(void){return *(regopen_fn volatile *)(main_base+iat_rva);}
static int exact_request(uintptr_t caller,HKEY key,LPCWSTR subkey,DWORD options,REGSAM access,PHKEY out){
    return caller==main_base+call_rva+5 && key==HKEY_LOCAL_MACHINE && subkey && options==0 && access==KEY_READ && out && wcscmp(subkey,target_key)==0;
}
static LSTATUS WINAPI registry_open_wrapper(HKEY key,LPCWSTR subkey,DWORD options,REGSAM access,PHKEY out){
    DWORD win=GetLastError();int wsa=WSAGetLastError();
    uintptr_t caller=(uintptr_t)__builtin_return_address(0);
    regopen_fn original=current_open();
    int scoped=exact_request(caller,key,subkey,options,access,out);
    int eligible=scoped && original==expected_hook;
    ULONGLONG begin=tick_now();
    restore_errors(win,wsa);
    LSTATUS result=original(key,subkey,options,access,out);
    DWORD last_win=GetLastError();int last_wsa=WSAGetLastError();
    LSTATUS first=result;unsigned retries=0;const char *stop="returned";
    if(eligible && result==ERROR_SHARING_VIOLATION){
        while(result==ERROR_SHARING_VIOLATION && retries+1<MAX_ATTEMPTS){
            if(tick_now()-begin>=RETRY_BUDGET_MS){stop="deadline";break;}
            /* The original hook has already returned and released its lock. */
            sleep_one();
            if(tick_now()-begin>=RETRY_BUDGET_MS){stop="deadline";break;}
            if(current_open()!=original){stop="iat_changed";break;}
            restore_errors(last_win,last_wsa);
            result=original(key,subkey,options,access,out);
            last_win=GetLastError();last_wsa=WSAGetLastError();++retries;
        }
        if(result==ERROR_SHARING_VIOLATION && retries+1==MAX_ATTEMPTS)stop="attempt_limit";
        log_line("keychip read-open first=%ld result=%ld retries=%u elapsed_ms=%llu stop=%s win=%lu wsa=%d",(long)first,(long)result,retries,(unsigned long long)(tick_now()-begin),stop,(unsigned long)last_win,last_wsa);
    }else if(scoped && !eligible && InterlockedCompareExchange(&mismatch_logged,1,0)==0){
        log_line("passthrough reason=current_iat_not_expected_hook current=%p expected=%p result=%ld",(void*)original,(void*)expected_hook,(long)result);
    }
    restore_errors(last_win,last_wsa);return result;
}
static int file_sha(const wchar_t *path,char out[65]){
    HANDLE f=CreateFileW(path,GENERIC_READ,FILE_SHARE_READ,NULL,OPEN_EXISTING,FILE_ATTRIBUTE_NORMAL,NULL);
    if(f==INVALID_HANDLE_VALUE)return 0;
    unsigned char buf[16384];DWORD got;BOOL ok;sha256_ctx sha;sha_init(&sha);
    do{ok=ReadFile(f,buf,sizeof(buf),&got,NULL);if(ok && got)sha_update(&sha,buf,got);}while(ok && got);
    CloseHandle(f);if(!ok)return 0;sha_final(&sha,out);return 1;
}
static int module_guard(HMODULE module,const wchar_t*name,const char*sha,uint32_t size,uint32_t timestamp,uint32_t entry){
    wchar_t path[32768];DWORD n=GetModuleFileNameW(module,path,32768);char hash[65];
    if(!n || n>=32768)return 0;const wchar_t *tail=wcsrchr(path,L'\\');tail=tail?tail+1:path;
    if(_wcsicmp(tail,name) || !file_sha(path,hash) || strcmp(hash,sha))return 0;
    uintptr_t base=(uintptr_t)module;const IMAGE_DOS_HEADER*dos=(const IMAGE_DOS_HEADER*)base;
    if(dos->e_magic!=IMAGE_DOS_SIGNATURE || dos->e_lfanew<64 || dos->e_lfanew>4096)return 0;
    const IMAGE_NT_HEADERS64*nt=(const IMAGE_NT_HEADERS64*)(base+(uintptr_t)dos->e_lfanew);
    return nt->Signature==IMAGE_NT_SIGNATURE && nt->FileHeader.Machine==IMAGE_FILE_MACHINE_AMD64 && nt->FileHeader.TimeDateStamp==timestamp && nt->OptionalHeader.Magic==IMAGE_NT_OPTIONAL_HDR64_MAGIC && nt->OptionalHeader.SizeOfImage==size && nt->OptionalHeader.AddressOfEntryPoint==entry;
}
static int signatures_ok(void){
    HMODULE main=GetModuleHandleW(NULL),hook=GetModuleHandleW(L"chusanamhook.dll");
    if(!main || !hook)return 0;main_base=(uintptr_t)main;hook_base=(uintptr_t)hook;
    if(!module_guard(main,L"amdaemon.exe",MAIN_SHA,MAIN_SIZE,MAIN_TIMESTAMP,MAIN_ENTRY) || !module_guard(hook,L"chusanamhook.dll",HOOK_SHA,HOOK_SIZE,HOOK_TIMESTAMP,HOOK_ENTRY))return 0;
    if(memcmp((const void*)(main_base+REG_FUNCTION_RVA),expected_reg_function,sizeof(expected_reg_function)) || memcmp((const void*)(hook_base+0x8b50),expected_hook_entry,sizeof(expected_hook_entry)))return 0;
    if(memcmp((const void*)(main_base+call_rva),original_call,6))return 0;
    int32_t displacement;memcpy(&displacement,(const void*)(main_base+call_rva+2),4);
    if(main_base+call_rva+6+displacement!=main_base+iat_rva)return 0;
    expected_hook=(regopen_fn)(hook_base+0x8b50);return 1;
}
static int emit_call(unsigned char out[6],uintptr_t site,uintptr_t destination){
    int64_t delta=(int64_t)destination-(int64_t)(site+5);if(delta<INT32_MIN || delta>INT32_MAX)return 0;
    int32_t relative=(int32_t)delta;out[0]=0xe8;memcpy(out+1,&relative,4);out[5]=0x90;return 1;
}
static unsigned char* allocate_near(uintptr_t site){
    SYSTEM_INFO si;GetSystemInfo(&si);uintptr_t step=si.dwAllocationGranularity,aligned=site&~(step-1);
    for(uintptr_t distance=step;distance<0x7fff0000u;distance+=step){uintptr_t addresses[2]={aligned>=distance?aligned-distance:0,aligned+distance};
        for(int i=0;i<2;i++){if(addresses[i]<(uintptr_t)si.lpMinimumApplicationAddress || addresses[i]>(uintptr_t)si.lpMaximumApplicationAddress-4096)continue;
            unsigned char*p=VirtualAlloc((void*)addresses[i],4096,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);if(p)return p;
        }
    }return NULL;
}
static void emit_thunk(unsigned char*buf,uintptr_t destination){const unsigned char op[6]={0xff,0x25,0,0,0,0};memcpy(buf,op,6);memcpy(buf+6,&destination,8);}
static int patch_call(void){
    unsigned char patch[6];uintptr_t site=main_base+call_rva;
    if(memcmp((const void*)site,original_call,6) || !emit_call(patch,site,(uintptr_t)thunk))return 0;
    DWORD old,ignored;if(!VirtualProtect((void*)site,6,PAGE_EXECUTE_READWRITE,&old))return 0;
    memcpy((void*)site,patch,6);BOOL flushed=FlushInstructionCache(GetCurrentProcess(),(void*)site,6);
#ifdef REGFIX_TEST
    if(test_fail_flush)flushed=FALSE;
#endif
    if(flushed && VirtualProtect((void*)site,6,old,&ignored))return 1;
    memcpy((void*)site,original_call,6);FlushInstructionCache(GetCurrentProcess(),(void*)site,6);VirtualProtect((void*)site,6,old,&ignored);return 0;
}
static int install_fix(HMODULE self){
    if(!signatures_ok()){log_line("disabled reason=main_or_amhook_identity_or_signature_mismatch");return 0;}
    thunk=allocate_near(main_base+call_rva);if(!thunk){log_line("disabled reason=near_allocation_failed");return 0;}
    emit_thunk(thunk,(uintptr_t)registry_open_wrapper);DWORD old;
    if(!VirtualProtect(thunk,4096,PAGE_EXECUTE_READ,&old) || !FlushInstructionCache(GetCurrentProcess(),thunk,14)){VirtualFree(thunk,0,MEM_RELEASE);thunk=NULL;log_line("disabled reason=thunk_rx_failed");return 0;}
    HMODULE pin=NULL;
    if(!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,(LPCWSTR)self,&pin)){VirtualFree(thunk,0,MEM_RELEASE);thunk=NULL;log_line("disabled reason=pin_failed");return 0;}
    if(!patch_call()){VirtualFree(thunk,0,MEM_RELEASE);thunk=NULL;log_line("disabled reason=call_patch_transaction_failed");return 0;}
    log_line("enabled version=1 call_rva=301B0B iat_rva=5B10D8 scope=HKLM_keychip_KEY_READ retry_error=32 deadline_ms=%u max_attempts=%u main_sha=%s hook_sha=%s expected_hook=%p iat_at_load=%p",RETRY_BUDGET_MS,MAX_ATTEMPTS,MAIN_SHA,HOOK_SHA,(void*)expected_hook,(void*)current_open());return 1;
}
#ifndef REGFIX_TEST
BOOL WINAPI DllMain(HINSTANCE self,DWORD reason,LPVOID reserved){
    (void)reserved;
    if(reason==DLL_PROCESS_ATTACH){DWORD win=GetLastError();int wsa=WSAGetLastError();DisableThreadLibraryCalls(self);InitializeCriticalSection(&log_lock);
        wchar_t path[32768];DWORD n=GetModuleFileNameW(self,path,32768);if(n && n<32768){wchar_t *tail=wcsrchr(path,L'\\');if(tail && (size_t)(tail-path)+80<32768){swprintf(tail+1,80,L"daemon-registry-fix-%lu.log",(unsigned long)GetCurrentProcessId());logfile=CreateFileW(path,FILE_APPEND_DATA,FILE_SHARE_READ,NULL,OPEN_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);}}
        if(logfile!=INVALID_HANDLE_VALUE)install_fix(self);restore_errors(win,wsa);
    }else if(reason==DLL_PROCESS_DETACH && logfile!=INVALID_HANDLE_VALUE){FlushFileBuffers(logfile);CloseHandle(logfile);logfile=INVALID_HANDLE_VALUE;}
    return TRUE;
}
#endif
