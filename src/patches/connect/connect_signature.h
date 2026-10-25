#ifndef CHUNI_CONNECT_SIGNATURE_H
#define CHUNI_CONNECT_SIGNATURE_H
#include <stddef.h>
#include <stdint.h>

enum { CC_SIGNATURE_RVA=0x67796c, CC_SHARED_RETURN_RVA=0x677997, CC_SIGNATURE_LENGTH=76 };
static const unsigned char cc_shared_signature[CC_SIGNATURE_LENGTH]={
    0x8d,0x45,0xd4,0xc7,0x45,0xd4,0x01,0x00,0x00,0x00,0x50,0x68,0x7e,0x66,0x04,0x80,
    0xff,0x76,0x1c,0xff,0x15,0xf4,0xab,0x21,0x02,0x6a,0x10,0x8d,0x86,0x04,0x01,0x00,
    0x00,0x50,0xff,0x76,0x1c,0xff,0x15,0x04,0xac,0x21,0x02,0x83,0xf8,0xff,0x75,0x5c,
    0xff,0x15,0x38,0xac,0x21,0x02,0x8b,0xf8,0x8b,0xcf,0x81,0xe9,0x33,0x27,0x00,0x00,
    0x74,0x5e,0x83,0xe9,0x02,0x74,0x59,0x83,0xe9,0x13,0x74,0x40
};
/* Validate every byte. Three absolute IAT operands are checked against the
   actual module base instead of being wildcarded. The connect operand must
   also equal the slot independently found in the import table. */
static inline int cc_shared_signature_matches(const unsigned char *code,size_t size,
                                              uintptr_t image_base,uintptr_t connect_slot) {
    static const unsigned offsets[3]={21,39,50};
    static const uint32_t slots[3]={0x1e1abf4U,0x1e1ac04U,0x1e1ac38U};
    if(!code || size<CC_SIGNATURE_LENGTH || image_base>UINT32_MAX-slots[2] ||
       connect_slot!=image_base+slots[1]) return 0;
    for(unsigned i=0;i<CC_SIGNATURE_LENGTH;i++) {
        unsigned expected=cc_shared_signature[i];
        for(unsigned j=0;j<3;j++) if(i>=offsets[j] && i<offsets[j]+4)
            expected=(unsigned)((image_base+slots[j])>>(8*(i-offsets[j])))&0xffU;
        if(code[i]!=expected) return 0;
    }
    return 1;
}
static inline int cc_shared_caller(int verified,uintptr_t caller,uintptr_t image_base) {
    return verified && caller==image_base+CC_SHARED_RETURN_RVA;
}
#endif
