#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <stdio.h>

#include "x11kvs.h"
#include "sha3/sph_blake.h"
#include "sha3/sph_bmw.h"
#include "sha3/sph_groestl.h"
#include "sha3/sph_jh.h"
#include "sha3/sph_keccak.h"
#include "sha3/sph_skein.h"
#include "sha3/sph_luffa.h"
#include "sha3/sph_cubehash.h"
#include "sha3/sph_shavite.h"
#include "sha3/sph_simd.h"
#include "sha3/sph_echo.h"
#include "sha256.h"


void sha256_double_hash(const char *input, char *output, unsigned int len)
{
    char temp[32];

    SHA256_CTX ctx;
    SHA256_Init(&ctx);
    SHA256_Update(&ctx, input, len);
    SHA256_Final((unsigned char*) &temp, &ctx);

    SHA256_Init(&ctx);
    SHA256_Update(&ctx, &temp, 32);
    SHA256_Final((unsigned char*) output, &ctx);
}

/* ----------- Sapphire 2.0 Hash X11KVS ------------------------------------ */
/* - X11, from the original 11 algos used on DASH -------------------------- */
/* - K, from Kyanite ------------------------------------------------------- */
/* - V, from Variable, variation of the number iterations on the X11K algo - */
/* - S, from Sapphire ------------------------------------------------------ */


const unsigned int HASHX11KV_MIN_NUMBER_ITERATIONS  = 2;
const unsigned int HASHX11KV_MAX_NUMBER_ITERATIONS  = 6;
const unsigned int HASHX11KV_NUMBER_ALGOS           = 11;

void x11kv(void *output, const void *input)
{
    sph_blake512_context      ctx_blake;
    sph_bmw512_context        ctx_bmw;
    sph_groestl512_context    ctx_groestl;
    sph_jh512_context         ctx_jh;
    sph_keccak512_context     ctx_keccak;
    sph_skein512_context      ctx_skein;
    sph_luffa512_context      ctx_luffa;
    sph_cubehash512_context   ctx_cubehash;
    sph_shavite512_context    ctx_shavite;
    sph_simd512_context       ctx_simd;
    sph_echo512_context       ctx_echo;
    //static unsigned char      pblank[1];


	//these uint512 in the c++ source of the client are backed by an array of uint32
    uint32_t hashA[16];


    // Iteration 0
    sph_blake512_init(&ctx_blake);
	sph_blake512 (&ctx_blake, input, 80);
    sph_blake512_close(&ctx_blake, hashA);

    int n = HASHX11KV_MIN_NUMBER_ITERATIONS + ( (unsigned int)((unsigned char*)hashA)[63] % (HASHX11KV_MAX_NUMBER_ITERATIONS - HASHX11KV_MIN_NUMBER_ITERATIONS + 1));	

    for (int i = 1; i < n; i++) {
        switch (((unsigned int)((unsigned char*)hashA)[i % 64]) % HASHX11KV_NUMBER_ALGOS) {
        case 0:
            sph_blake512_init(&ctx_blake);
            sph_blake512(&ctx_blake, hashA, 64);
            sph_blake512_close(&ctx_blake, hashA);
            break;
        case 1:
            sph_bmw512_init(&ctx_bmw);
            sph_bmw512(&ctx_bmw, hashA, 64);
            sph_bmw512_close(&ctx_bmw, hashA);
            break;
        case 2:
            sph_groestl512_init(&ctx_groestl);
            sph_groestl512(&ctx_groestl, hashA, 64);
            sph_groestl512_close(&ctx_groestl, hashA);
            break;
        case 3:
            sph_skein512_init(&ctx_skein);
            sph_skein512(&ctx_skein, hashA, 64);
            sph_skein512_close(&ctx_skein, hashA);
            break;
        case 4:
            sph_jh512_init(&ctx_jh);
            sph_jh512(&ctx_jh, hashA, 64);
            sph_jh512_close(&ctx_jh, hashA);
            break;
        case 5:
            sph_keccak512_init(&ctx_keccak);
            sph_keccak512(&ctx_keccak, hashA, 64);
            sph_keccak512_close(&ctx_keccak, hashA);
            break;
        case 6:
            sph_luffa512_init(&ctx_luffa);
            sph_luffa512(&ctx_luffa, hashA, 64);
            sph_luffa512_close(&ctx_luffa, hashA);
            break;
        case 7:
            sph_cubehash512_init(&ctx_cubehash);
            sph_cubehash512(&ctx_cubehash, hashA, 64);
            sph_cubehash512_close(&ctx_cubehash, hashA);
            break;
        case 8:
            sph_shavite512_init(&ctx_shavite);
            sph_shavite512(&ctx_shavite, hashA, 64);
            sph_shavite512_close(&ctx_shavite, hashA);
            break;
        case 9:
            sph_simd512_init(&ctx_simd);
            sph_simd512(&ctx_simd, hashA, 64);
            sph_simd512_close(&ctx_simd, hashA);
            break;
        case 10:
            sph_echo512_init(&ctx_echo);
            sph_echo512(&ctx_echo, hashA, 64);
            sph_echo512_close(&ctx_echo, hashA);
            break;
        }
    }
	memcpy(output, hashA, 32);
}

const unsigned int HASHX11KVS_MAX_LEVEL = 7;
const unsigned int HASHX11KVS_MIN_LEVEL = 1;
const unsigned int HASHX11KVS_MAX_DRIFT = 0xFFFF;

void x11kvshash(char *output, const char *input, unsigned int level)
{
    void *hash = malloc(32);
	x11kv(hash, input);
    
	if (level == HASHX11KVS_MIN_LEVEL)
	{
		memcpy(output, hash, 32);
		return;
	}

    uint32_t nonce = le32dec(input + 76);

    uint8_t nextheader1[80];
    uint8_t nextheader2[80];

    uint32_t nextnonce1 = nonce + (le32dec(hash + 24) % HASHX11KVS_MAX_DRIFT);
    uint32_t nextnonce2 = nonce + (le32dec(hash + 28) % HASHX11KVS_MAX_DRIFT);

    memcpy(nextheader1, input, 76);
    le32enc(nextheader1 + 76, nextnonce1);

    memcpy(nextheader2, input, 76);
    le32enc(nextheader2 + 76, nextnonce2);

	void *hash1 = malloc(32);
	void *hash2 = malloc(32);
	void *nextheader1Pointer = malloc(80);
	void *nextheader2Pointer = malloc(80);

	memcpy(nextheader1Pointer, nextheader1, 80);
	memcpy(nextheader2Pointer, nextheader2, 80);

    
	x11kvshash(hash1, nextheader1Pointer, level - 1);
    	x11kvshash(hash2, nextheader2Pointer, level - 1);


	// Concat hash, hash1 and hash2
	void *hashConcated = malloc(32 + 32 + 32);
	memcpy(hashConcated, hash, 32);
	memcpy(hashConcated + 32, hash1, 32);
	memcpy(hashConcated + 32 + 32, hash2, 32);

	sha256_double_hash(hashConcated, output, 96);

	free(hash);
	free(hash1);
	free(hash2);
	free(nextheader1Pointer);
	free(nextheader2Pointer);
}

void x11kvs_hash(const char* input, char* output,  uint32_t len)
{
	x11kvshash(output, input, HASHX11KVS_MAX_LEVEL);
}