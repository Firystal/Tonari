#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "connect_signature.h"
int main(void) {
    unsigned char code[CC_SIGNATURE_LENGTH]; unsigned checks=0;
    #define CHECK(x) do { assert(x); ++checks; } while(0)
    uintptr_t base=0x400000U,slot=base+0x1e1ac04U;
    memcpy(code,cc_shared_signature,sizeof(code));
    CHECK(cc_shared_signature_matches(code,sizeof(code),base,slot));
    CHECK(!cc_shared_signature_matches(NULL,sizeof(code),base,slot));
    CHECK(!cc_shared_signature_matches(code,sizeof(code)-1,base,slot));
    CHECK(!cc_shared_signature_matches(code,sizeof(code),base,slot+4));
    CHECK(!cc_shared_signature_matches(code,sizeof(code),0xff000000U,0));
    for(unsigned i=0;i<sizeof(code);i++) {
        code[i]^=1; CHECK(!cc_shared_signature_matches(code,sizeof(code),base,slot)); code[i]^=1;
    }
    CHECK(!cc_shared_signature_matches(code,sizeof(code),base+0x10000,slot+0x10000));
    static const unsigned offsets[3]={21,39,50};
    static const uint32_t slots[3]={0x1e1abf4U,0x1e1ac04U,0x1e1ac38U};
    for(unsigned j=0;j<3;j++) for(unsigned k=0;k<4;k++) code[offsets[j]+k]=(unsigned char)((base+0x10000+slots[j])>>(8*k));
    CHECK(cc_shared_signature_matches(code,sizeof(code),base+0x10000,slot+0x10000));
    CHECK(cc_shared_caller(1,base+CC_SHARED_RETURN_RVA,base));
    CHECK(!cc_shared_caller(0,base+CC_SHARED_RETURN_RVA,base));
    CHECK(!cc_shared_caller(1,base+CC_SHARED_RETURN_RVA-1,base));
    CHECK(!cc_shared_caller(1,base+CC_SHARED_RETURN_RVA+1,base));
    CHECK(!cc_shared_caller(1,base+CC_SHARED_RETURN_RVA+0x10000,base));
    printf("PASS %u signature/caller guard checks, including mutation of every signature byte.\n",checks);
    return 0;
}
