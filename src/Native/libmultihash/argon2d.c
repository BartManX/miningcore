#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>

#include "sysendian.h"

#include "argon2d/argon2.h"
#include "argon2d/core.h"

static const size_t INPUT_BYTES = 80;  // Lenth of a block header in bytes. Input Length = Salt Length (salt = input)
static const size_t OUTPUT_BYTES = 32; // Length of output needed for a 256-bit hash
static const unsigned int DEFAULT_ARGON2_FLAG = 2; //Same as ARGON2_DEFAULT_FLAGS

void argon2d250_call(const void *input, void *output)
{
	argon2_context context;
	context.out = (uint8_t *)output;
	context.outlen = (uint32_t)OUTPUT_BYTES;
	context.pwd = (uint8_t *)input;
	context.pwdlen = (uint32_t)INPUT_BYTES;
	context.salt = (uint8_t *)input; //salt = input
	context.saltlen = (uint32_t)INPUT_BYTES;
	context.secret = NULL;
	context.secretlen = 0;
	context.ad = NULL;
	context.adlen = 0;
	context.allocate_cbk = NULL;
	context.free_cbk = NULL;
	context.flags = DEFAULT_ARGON2_FLAG; // = ARGON2_DEFAULT_FLAGS
	// main configurable Argon2 hash parameters
	context.m_cost = 250;  // Memory in KiB (250KB)
	context.lanes = 4;     // Degree of Parallelism
	context.threads = 1;   // Threads
	context.t_cost = 1;    // Iterations
        context.version = ARGON2_VERSION_10;
	
	argon2_ctx(&context, Argon2_d);
}
void argon2d500_call(const void *input, void *output)
{
    	argon2_context context;
    	context.out = (uint8_t *)output;
    	context.outlen = (uint32_t)OUTPUT_BYTES;
    	context.pwd = (uint8_t *)input;
    	context.pwdlen = (uint32_t)INPUT_BYTES;
    	context.salt = (uint8_t *)input; //salt = input
    	context.saltlen = (uint32_t)INPUT_BYTES;
    	context.secret = NULL;
    	context.secretlen = 0;
    	context.ad = NULL;
    	context.adlen = 0;
    	context.allocate_cbk = NULL;
    	context.free_cbk = NULL;
    	context.flags = DEFAULT_ARGON2_FLAG; // = ARGON2_DEFAULT_FLAGS
    	// main configurable Argon2 hash parameters
    	context.m_cost = 500;  // Memory in KiB (512KB)
    	context.lanes = 8;     // Degree of Parallelism
    	context.threads = 1;   // Threads
    	context.t_cost = 2;    // Iterations
        context.version = ARGON2_VERSION_10;
	
	argon2_ctx(&context, Argon2_d);
}
void argon2d1000_call(const void *input, void *output)
{
    	argon2_context context;
    	context.out = (uint8_t *)output;
    	context.outlen = (uint32_t)OUTPUT_BYTES;
    	context.pwd = (uint8_t *)input;
    	context.pwdlen = (uint32_t)INPUT_BYTES;
    	context.salt = (uint8_t *)input; //salt = input
    	context.saltlen = (uint32_t)INPUT_BYTES;
    	context.secret = NULL;
    	context.secretlen = 0;
    	context.ad = NULL;
    	context.adlen = 0;
    	context.allocate_cbk = NULL;
    	context.free_cbk = NULL;
    	context.flags = DEFAULT_ARGON2_FLAG; // = ARGON2_DEFAULT_FLAGS
    	// main configurable Argon2 hash parameters
    	context.m_cost = 1000;  // Memory in KiB (1000KiB)
	context.lanes = 8;     // Degree of Parallelism
	context.threads = 1;   // Threads
	context.t_cost = 2;    // Iterations	
        context.version = ARGON2_VERSION_10;

	argon2_ctx(&context, Argon2_d);
}
void argon2d16000_call(const void *input, void *output)
{
	argon2_context context;
	context.out = (uint8_t *)output;
	context.outlen = (uint32_t)OUTPUT_BYTES;
	context.pwd = (uint8_t *)input;
	context.pwdlen = (uint32_t)INPUT_BYTES;
	context.salt = (uint8_t *)input; //salt = input
	context.saltlen = (uint32_t)INPUT_BYTES;
	context.secret = NULL;
	context.secretlen = 0;
	context.ad = NULL;
	context.adlen = 0;
	context.allocate_cbk = NULL;
	context.free_cbk = NULL;
	context.flags = DEFAULT_ARGON2_FLAG; // = ARGON2_DEFAULT_FLAGS
	// main configurable Argon2 hash parameters
	context.m_cost = 16000; // Memory in KiB (~16384KB)
	context.lanes = 1;    // Degree of Parallelism
	context.threads = 1;   // Threads
	context.t_cost = 1;    // Iterations
	context.version = ARGON2_VERSION_10;

	argon2_ctx(&context, Argon2_d);
}

void argon2d250_hash(const char* input, char* output, uint32_t len)
{
	argon2d250_call(input, output);
}

void argon2d500_hash(const char* input, char* output, uint32_t len)
{
	argon2d500_call(input, output);
}

void argon2d1000_hash(const char* input, char* output, uint32_t len)
{
	argon2d1000_call(input, output);
}

void argon2d16000_hash(const char* input, char* output, uint32_t len)
{
	argon2d16000_call(input, output);
}

