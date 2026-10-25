#define REGFIX_TEST
#include "daemon_registry_fix.c"
#include <stdlib.h>
static unsigned checks,calls,alternate_calls;
static LSTATUS sequence[4];static unsigned seq_count;
static int argument_ok,error_input_ok,change_after_first;
static ULONGLONG mock_call_advance;
static HKEY expected_key;
static LPCWSTR expected_subkey;
static DWORD expected_options;
static REGSAM expected_access;
static PHKEY expected_out;
static void check(int ok,const char*label){if(!ok){fprintf(stderr,"FAIL %s\n",label);exit(1);}printf("PASS %s\n",label);checks++;}
static LSTATUS WINAPI alternate(HKEY key,LPCWSTR subkey,DWORD options,REGSAM access,PHKEY out){(void)key;(void)subkey;(void)options;(void)access;(void)out;++alternate_calls;SetLastError(9876);return ERROR_SHARING_VIOLATION;}
static LSTATUS WINAPI mock_open(HKEY key,LPCWSTR subkey,DWORD options,REGSAM access,PHKEY out){
    DWORD win=GetLastError();int wsa=WSAGetLastError();
    argument_ok &= key==expected_key && subkey==expected_subkey && options==expected_options && access==expected_access && out==expected_out;
    error_input_ok &= win==(calls?5678+calls:1234) && wsa==(int)win;
    unsigned index=calls<seq_count?calls:seq_count-1;LSTATUS result=sequence[index];++calls;
    if(result==0 && out)*out=(HKEY)(uintptr_t)0xbeef;
    if(change_after_first && calls==1)*(regopen_fn*)(main_base+iat_rva)=alternate;
    test_clock+=mock_call_advance;SetLastError(5678+calls);return result;
}
static void reset_case(PHKEY out){
    calls=alternate_calls=test_sleep_count=0;test_clock=0;test_sleep_step=1;mock_call_advance=0;change_after_first=0;test_sleep_iat=NULL;
    argument_ok=error_input_ok=1;expected_key=HKEY_LOCAL_MACHINE;expected_subkey=target_key;expected_options=0;expected_access=KEY_READ;expected_out=out;
    *(regopen_fn*)(main_base+iat_rva)=mock_open;expected_hook=mock_open;mismatch_logged=0;
    sequence[0]=ERROR_SHARING_VIOLATION;seq_count=1;
}
static LSTATUS call_fixture(regopen_fn fn){SetLastError(1234);LSTATUS ret=fn(expected_key,expected_subkey,expected_options,expected_access,expected_out);DWORD win=GetLastError();int wsa=WSAGetLastError();check(win==(alternate_calls?9876:5678+calls) && wsa==(int)win,"final original Win32/WSA error preserved");return ret;}
int wmain(int argc,wchar_t **argv){
    (void)install_fix;
    char digest[65];sha256_ctx sha;sha_init(&sha);sha_update(&sha,"abc",3);sha_final(&sha,digest);check(!strcmp(digest,"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"),"SHA256 vector");
    if(argc!=1 && argc!=3){fprintf(stderr,"Usage: test_registry_fix.exe [main-image hook-image]\n");return 2;}
    if(argc==3){check(file_sha(argv[1],digest)&&!strcmp(digest,MAIN_SHA),"explicit main image hash");check(file_sha(argv[2],digest)&&!strcmp(digest,HOOK_SHA),"explicit hook image hash");}
    unsigned char encoded[6];uintptr_t site=0x200000000ULL;check(emit_call(encoded,site,site+5+INT32_MAX)&&encoded[5]==0x90,"rel32 upper and NOP");check(emit_call(encoded,site,site+5-0x80000000ULL),"rel32 lower");check(!emit_call(encoded,site,site+5+0x80000000ULL)&&!emit_call(encoded,site,site+4-0x80000000ULL),"rel32 rejects out of range");
    main_base=(uintptr_t)VirtualAlloc(NULL,0x600000,MEM_RESERVE|MEM_COMMIT,PAGE_READWRITE);check(main_base!=0,"isolated fixture memory");
    InitializeCriticalSection(&log_lock);logfile=CreateFileW(L"registry-fixture.log",GENERIC_WRITE,FILE_SHARE_READ,NULL,CREATE_ALWAYS,FILE_ATTRIBUTE_NORMAL,NULL);check(logfile!=INVALID_HANDLE_VALUE,"fixture log opened");
    /* x64 function stub copies its fifth incoming argument into the nested
       call's shadow-space slot. The replacement CALL returns to the NOP. */
    unsigned char*stub=(unsigned char*)(main_base+call_rva-14);
    const unsigned char pre[14]={0x48,0x83,0xec,0x38,0x48,0x8b,0x44,0x24,0x60,0x48,0x89,0x44,0x24,0x20};
    const unsigned char post[5]={0x48,0x83,0xc4,0x38,0xc3};memcpy(stub,pre,14);memcpy(stub+14,original_call,6);memcpy(stub+20,post,5);
    thunk=(unsigned char*)(main_base+0x10000);emit_thunk(thunk,(uintptr_t)registry_open_wrapper);DWORD old;
    check(VirtualProtect(thunk,4096,PAGE_EXECUTE_READ,&old)&&FlushInstructionCache(GetCurrentProcess(),thunk,14),"thunk RW to RX");
    uintptr_t page=(main_base+call_rva)&~(uintptr_t)4095;check(VirtualProtect((void*)page,4096,PAGE_EXECUTE_READ,&old),"fixture call page RX");
    regopen_fn iat_before=mock_open;*(regopen_fn*)(main_base+iat_rva)=iat_before;
    test_fail_flush=1;check(!patch_call()&&!memcmp((void*)(main_base+call_rva),original_call,6),"failed patch rolls back six bytes");MEMORY_BASIC_INFORMATION mbi;VirtualQuery((void*)page,&mbi,sizeof(mbi));check(mbi.Protect==PAGE_EXECUTE_READ&&current_open()==iat_before,"rollback preserves RX and IAT");
    test_fail_flush=0;check(patch_call()&&current_open()==iat_before,"single call patch leaves IAT unchanged");check(!patch_call(),"double patch rejected without changes");
    regopen_fn native=(regopen_fn)stub;HKEY value=(HKEY)(uintptr_t)0xabcd;
    reset_case(&value);sequence[0]=32;sequence[1]=32;sequence[2]=0;seq_count=3;check(call_fixture(native)==0&&calls==3&&test_sleep_count==2&&value==(HKEY)(uintptr_t)0xbeef&&argument_ok&&error_input_ok,"native five-argument 32 32 success");
    reset_case(&value);sequence[0]=32;sequence[1]=ERROR_ACCESS_DENIED;seq_count=2;check(call_fixture(native)==ERROR_ACCESS_DENIED&&calls==2&&test_sleep_count==1&&error_input_ok,"32 then other failure stays failure");
    reset_case(&value);sequence[0]=ERROR_ACCESS_DENIED;check(call_fixture(native)==ERROR_ACCESS_DENIED&&calls==1&&!test_sleep_count,"non32 never retries");
    reset_case(&value);sequence[0]=0;check(call_fixture(native)==0&&calls==1&&!test_sleep_count,"success never retries");
    reset_case(&value);test_sleep_step=2500;value=(HKEY)(uintptr_t)0xabcd;check(call_fixture(native)==32&&calls==1&&test_sleep_count==1&&value==(HKEY)(uintptr_t)0xabcd,"sleep crosses deadline no additional call or fake success");
    reset_case(&value);test_sleep_step=17;check(call_fixture(native)==32&&test_clock>=2000&&calls<2000,"persistent32 bounded by deadline");
    reset_case(&value);test_sleep_step=0;check(call_fixture(native)==32&&calls==MAX_ATTEMPTS&&test_sleep_count==MAX_ATTEMPTS-1,"persistent32 bounded by total attempt cap");
    reset_case(&value);mock_call_advance=2500;check(call_fixture(native)==32&&calls==1&&!test_sleep_count,"first call exhausts deadline");
    reset_case(&value);test_clock=UINT64_MAX-1;sequence[0]=32;sequence[1]=32;sequence[2]=0;seq_count=3;check(call_fixture(native)==0&&calls==3,"unsigned tick wrap elapsed comparison");
    reset_case(&value);change_after_first=1;check(call_fixture(native)==32&&calls==1&&!alternate_calls,"IAT change stops retry without calling replacement");
    reset_case(&value);test_sleep_iat=alternate;check(call_fixture(native)==32&&calls==1&&!alternate_calls&&test_sleep_count==1,"IAT changes during sleep stops before next call");
    reset_case(&value);*(regopen_fn*)(main_base+iat_rva)=alternate;check(call_fixture(native)==32&&!calls&&alternate_calls==1&&!test_sleep_count,"unexpected current IAT passed through once");
    reset_case(&value);expected_key=HKEY_CURRENT_USER;check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"different root passes through");
    reset_case(&value);expected_subkey=L"System\\Sega\\SystemProperty\\other";check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"different subkey passes through");
    reset_case(&value);expected_subkey=NULL;check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"null subkey passes through");
    reset_case(&value);expected_options=1;check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"different options pass through");
    reset_case(&value);expected_access=KEY_WRITE;check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"write access passes through");
    reset_case(NULL);check(call_fixture(native)==32&&calls==1&&!test_sleep_count&&argument_ok,"null output passes through");
    reset_case(&value);check(call_fixture(registry_open_wrapper)==32&&calls==1&&!test_sleep_count,"different caller passes through");
    uintptr_t fixture_base=main_base;unsigned char call_before[6];memcpy(call_before,(void*)(fixture_base+call_rva),6);regopen_fn pointer_before=current_open();
    check(!install_fix(GetModuleHandleW(NULL)),"wrong executable identity disables installation");
    check(!memcmp(call_before,(void*)(fixture_base+call_rva),6)&&*(regopen_fn*)(fixture_base+iat_rva)==pointer_before,"identity rejection changes no fixture code or IAT");main_base=fixture_base;
    FlushFileBuffers(logfile);CloseHandle(logfile);logfile=INVALID_HANDLE_VALUE;DeleteCriticalSection(&log_lock);VirtualFree((void*)main_base,0,MEM_RELEASE);
    printf("All %u checks passed; no target process opened, injected or changed.\n",checks);return 0;
}
