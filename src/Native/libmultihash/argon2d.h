#ifndef ARGON2D_H
#define ARGON2D_H

#ifdef __cplusplus
extern "C" {
#endif

#include <stdint.h>

void argon2d250_hash(const char* input, char* output, uint32_t len);
void argon2d500_hash(const char* input, char* output, uint32_t len);
void argon2d1000_hash(const char* input, char* output, uint32_t len);
void argon2d16000_hash(const char* input, char* output, uint32_t len);

#ifdef __cplusplus
}
#endif

#endif