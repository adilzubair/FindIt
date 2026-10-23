#pragma once
#include <stdint.h>
#if defined(_WIN32)
#define FINDIT_API __declspec(dllexport)
#else
#define FINDIT_API
#endif
#ifdef __cplusplus
extern "C" {
#endif
typedef struct engine fi_engine;
/* ABI v1: UTF-8 paths/text. One operation per engine at a time. Inference does not own input/output buffers.
 * error is either null or an allocated UTF-8 string; caller MUST release it with fi_free_error.
 * cancel points to an aligned 32-bit flag valid until inference returns; nonzero requests cancellation.
 * Embedding functions return 768 on success or -1 on failure. capacity counts floats, not bytes.
 * No C++ exception crosses this boundary. Heavy media and GPU resources are released by fi_close.
 */
FINDIT_API fi_engine* fi_create(const char* text_model,const char* media_model,const char* backend_directory,int threads,int gpu,char** error);
FINDIT_API void fi_close(fi_engine* engine);
FINDIT_API void fi_clear_media_cache(fi_engine* engine);
FINDIT_API void fi_free_error(char* error);
FINDIT_API int fi_gpu_available(const char* backend_directory);
FINDIT_API int fi_embed_text(fi_engine* engine,const char* text,int query,float* output,int capacity,volatile int32_t* cancel,char** error);
FINDIT_API int fi_embed_media(fi_engine* engine,const char* path,int image,float* output,int capacity,volatile int32_t* cancel,char** error);
/* newline-separated scratch frame paths, in chronological order, at most eight. */
FINDIT_API int fi_embed_frames(fi_engine* engine,const char* paths,float* output,int capacity,volatile int32_t* cancel,char** error);
/* ABI v1.1: the same inputs from caller-owned memory, avoiding scratch files: one encoded image or WAV,
 * or count encoded frames in chronological order, at most eight. Buffers are only read during the call. */
FINDIT_API int fi_embed_media_buffer(fi_engine* engine,const unsigned char* data,size_t length,int image,float* output,int capacity,volatile int32_t* cancel,char** error);
FINDIT_API int fi_embed_frame_buffers(fi_engine* engine,const unsigned char* const* frames,const size_t* lengths,int count,float* output,int capacity,volatile int32_t* cancel,char** error);
#ifdef __cplusplus
}
#endif
