#include "llama.h"
#include "mtmd.h"
#include "mtmd-helper.h"
#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <cstring>
#include <deque>
#include <filesystem>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>
#define API extern "C" __declspec(dllexport)
struct engine {
    struct projection {std::string key;std::vector<float> rows;};
    llama_model* model=nullptr;
    llama_context* context=nullptr;
    mtmd_context* media=nullptr;
    llama_batch_ext* batch=nullptr;
    int maximum=512;
    std::deque<projection> projections;
    size_t projection_bytes=0;
    ~engine(){if(batch)llama_batch_ext_free(batch);if(media)mtmd_free(media);if(context)llama_free(context);if(model)llama_model_free(model);}
};
static void error_out(char** error,const std::string& message){if(error){*error=(char*)std::malloc(message.size()+1);if(*error)std::memcpy(*error,message.c_str(),message.size()+1);}}
static bool cancelled(void* state){return state && *(volatile int32_t*)state != 0;}
API void fi_free_error(char* error){std::free(error);}
API void fi_close(engine* handle){delete handle;}
API void fi_clear_media_cache(engine* e){if(e){e->projections.clear();e->projection_bytes=0;}}
API engine* fi_create(const char* text,const char* media,const char* backends,int threads,int gpu,char** error){
    if(error)*error=nullptr;
    try{
        llama_backend_init();ggml_backend_load_all_from_path(backends);
        if(gpu){auto folder=std::filesystem::u8path(backends)/"gpu";auto utf8=folder.u8string();ggml_backend_load_all_from_path(utf8.c_str());}
        auto e=std::make_unique<engine>();
        auto mp=llama_model_default_params();mp.n_gpu_layers=gpu?999:0;
        e->model=llama_model_load_from_file(text,mp);if(!e->model)throw std::runtime_error("Cannot load Gemma text GGUF.");
        e->maximum=media?2048:512;
        auto cp=llama_context_default_params();cp.n_ctx=e->maximum;cp.n_batch=e->maximum;cp.n_ubatch=e->maximum;cp.n_seq_max=1;cp.kv_unified=true;cp.embeddings=true;cp.pooling_type=LLAMA_POOLING_TYPE_MEAN;cp.n_threads=threads;cp.n_threads_batch=threads;
        e->context=llama_init_from_model(e->model,cp);if(!e->context)throw std::runtime_error("Cannot create embedding context.");
        e->batch=llama_batch_ext_init(e->context);
        if(media){auto p=mtmd_context_params_default();p.use_gpu=gpu!=0;p.print_timings=false;p.warmup=false;p.n_threads=threads;p.image_min_tokens=70;p.image_max_tokens=140;e->media=mtmd_init_from_file(media,e->model,p);if(!e->media)throw std::runtime_error("Cannot load Gemma media GGUF.");}
        return e.release();
    }catch(const std::exception& ex){error_out(error,ex.what());return nullptr;}
}
API int fi_gpu_available(const char* backends){
    try{auto folder=std::filesystem::u8path(backends)/"gpu";auto utf8=folder.u8string();ggml_backend_load_all_from_path(utf8.c_str());
        for(size_t i=0;i<ggml_backend_dev_count();++i){auto type=ggml_backend_dev_type(ggml_backend_dev_get(i));if(type==GGML_BACKEND_DEVICE_TYPE_GPU||type==GGML_BACKEND_DEVICE_TYPE_IGPU)return 1;}
    }catch(...){}return 0;
}
static void add_token(engine* e,llama_token token,int& position){int i=llama_batch_ext_add_token(e->batch,0,token);llama_pos pos[4]={position++,0,0,0};if(i<0||!llama_batch_ext_set_pos(e->batch,i,pos)||!llama_batch_ext_set_output_embd(e->batch,i,true))throw std::runtime_error("Embedding batch capacity exceeded.");}
static int finish(engine* e,float* result,int capacity,volatile int32_t* cancel){
    llama_set_abort_callback(e->context,cancelled,(void*)cancel);
    int status=llama_process(e->context,LLAMA_PROCESS_TYPE_ENCODE,e->batch);
    llama_set_abort_callback(e->context,nullptr,nullptr);
    if(cancel && *cancel)throw std::runtime_error("Embedding cancelled.");
    if(status!=0)throw std::runtime_error("llama_process ENCODE failed: "+std::to_string(status));
    int dimensions=llama_model_n_embd_out(e->model);if(capacity<dimensions)throw std::runtime_error("Embedding result buffer is too small.");
    auto pooled=llama_get_embeddings_seq(e->context,0);if(!pooled)throw std::runtime_error("Model returned no mean-pooled vector.");
    double square=0;for(int i=0;i<dimensions;++i)square+=double(pooled[i])*pooled[i];
    if(!std::isfinite(square)||square<=0)throw std::runtime_error("Model returned non-finite/zero vector.");
    float scale=float(1/std::sqrt(square));for(int i=0;i<dimensions;++i)result[i]=pooled[i]*scale;
    return dimensions;
}
static float* encode_rows(engine* e,const mtmd_input_chunk* chunk){
    const char* id=mtmd_input_chunk_get_id(chunk);auto count=mtmd_input_chunk_get_n_tokens(chunk);int width=llama_model_n_embd_inp(e->model);
    std::string key=id?std::string(id)+":"+std::to_string(count)+":"+std::to_string(width):"";
    if(!key.empty())for(auto& entry:e->projections)if(entry.key==key)return entry.rows.data();
    if(mtmd_encode_chunk(e->media,chunk)!=0)throw std::runtime_error("Media encoder failed.");auto rows=mtmd_get_output_embd(e->media);
    size_t bytes=count*size_t(width)*sizeof(float);
    if(key.empty()||bytes>16*1024*1024)return rows;
    while(!e->projections.empty()&&(e->projections.size()>=12||e->projection_bytes+bytes>16*1024*1024)){e->projection_bytes-=e->projections.front().rows.size()*sizeof(float);e->projections.pop_front();}
    e->projections.push_back({key,std::vector<float>(rows,rows+count*size_t(width))});e->projection_bytes+=bytes;return e->projections.back().rows.data();
}
API int fi_embed_text(engine* e,const char* text,int query,float* result,int capacity,volatile int32_t* cancel,char** error){
    if(error)*error=nullptr;
    try{if(!e)throw std::runtime_error("Null engine.");llama_batch_ext_clear(e->batch);
        std::string input=query?"task: search result | query: "+std::string(text):"title: none | text: "+std::string(text);
        auto vocab=llama_model_get_vocab(e->model);std::vector<llama_token> tokens(input.size()+8);
        int count=llama_tokenize(vocab,input.c_str(),int(input.size()),tokens.data(),int(tokens.size()),true,true);
        if(count<0){tokens.resize(-count);count=llama_tokenize(vocab,input.c_str(),int(input.size()),tokens.data(),int(tokens.size()),true,true);}
        if(count<0)throw std::runtime_error("Tokenization failed.");tokens.resize(count);
        if(count>e->maximum){tokens.resize(e->maximum);tokens.back()=llama_vocab_eos(vocab);}
        int pos=0;for(auto t:tokens)add_token(e,t,pos);return finish(e,result,capacity,cancel);
    }catch(const std::exception& ex){if(e)llama_batch_ext_clear(e->batch);error_out(error,ex.what());return -1;}
}
// Encoded input comes from a scratch file or, without that round trip, from caller-owned memory.
// Both decode through mtmd_helper_bitmap_init_from_buf, whose content hash keys the projection cache.
struct encoded {const char* path;const unsigned char* data;size_t length;};
static mtmd_bitmap* decode(engine* e,const encoded& input){
    if(!input.path&&(!input.data||input.length==0))throw std::runtime_error("Empty media input.");
    // Managed caller uses ASCII scratch names to avoid narrow fopen path encoding in upstream mtmd.
    return input.path?mtmd_helper_bitmap_init_from_file(e->media,input.path,false,mtmd_helper_init_opt_default()).bitmap
        :mtmd_helper_bitmap_init_from_buf(e->media,input.data,input.length,false,mtmd_helper_init_opt_default()).bitmap;
}
static int embed_media(engine* e,const encoded& input,float* result,int capacity,volatile int32_t* cancel,char** error){
    if(error)*error=nullptr;
    mtmd_bitmap* bitmap=nullptr;mtmd_input_chunks* chunks=nullptr;
    try{if(!e||!e->media)throw std::runtime_error("Media encoder is unavailable.");
        bitmap=decode(e,input);if(!bitmap)throw std::runtime_error("Cannot decode media input.");
        chunks=mtmd_input_chunks_init();const mtmd_bitmap* bitmaps[]={bitmap};const char* marker=mtmd_default_marker();
        mtmd_input_text text={marker,std::strlen(marker),true,true};
        if(mtmd_tokenize(e->media,chunks,&text,bitmaps,1)!=0)throw std::runtime_error("Cannot tokenize media input.");
        llama_batch_ext_clear(e->batch);int position=0;
        for(size_t i=0;i<mtmd_input_chunks_size(chunks);++i){if(cancel&&*cancel)throw std::runtime_error("Embedding cancelled.");
            auto chunk=mtmd_input_chunks_get(chunks,i);
            if(mtmd_input_chunk_get_type(chunk)==MTMD_INPUT_CHUNK_TYPE_TEXT){size_t n=0;auto tokens=mtmd_input_chunk_get_tokens_text(chunk,&n);for(size_t j=0;j<n;++j)add_token(e,tokens[j],position);}
            else{auto rows=encode_rows(e,chunk);int width=llama_model_n_embd_inp(e->model);
                for(size_t j=0;j<mtmd_input_chunk_get_n_tokens(chunk);++j){llama_embd row={rows+j*width,1,size_t(width)};int index=llama_batch_ext_add_embd(e->batch,0,row);llama_pos pos[4]={position++,0,0,0};if(index<0||!llama_batch_ext_set_pos(e->batch,index,pos)||!llama_batch_ext_set_output_embd(e->batch,index,true))throw std::runtime_error("Media input exceeds context.");}}
        }
        int n=finish(e,result,capacity,cancel);mtmd_input_chunks_free(chunks);mtmd_bitmap_free(bitmap);return n;
    }catch(const std::exception& ex){if(chunks)mtmd_input_chunks_free(chunks);if(bitmap)mtmd_bitmap_free(bitmap);if(e)llama_batch_ext_clear(e->batch);error_out(error,ex.what());return -1;}
}
API int fi_embed_media(engine* e,const char* path,int image,float* result,int capacity,volatile int32_t* cancel,char** error){
    if(!path){if(error)*error=nullptr;error_out(error,"Null media path.");return -1;}
    return embed_media(e,{path,nullptr,0},result,capacity,cancel,error);
}
API int fi_embed_media_buffer(engine* e,const unsigned char* data,size_t length,int image,float* result,int capacity,volatile int32_t* cancel,char** error){
    return embed_media(e,{nullptr,data,length},result,capacity,cancel,error);
}
// Ordered frames pass through one shared non-causal sequence, allowing attention across frame boundaries.
static int embed_frames(engine* e,const std::vector<encoded>& frames,float* result,int capacity,volatile int32_t* cancel,char** error){
    if(error)*error=nullptr;
    std::vector<mtmd_bitmap*> bitmaps;mtmd_input_chunks* chunks=nullptr;
    try{
        if(!e||!e->media)throw std::runtime_error("Media encoder is unavailable.");
        if(frames.empty()||frames.size()>8)throw std::runtime_error("Clip requires 1 to 8 ordered frames.");
        std::string prompt;
        for(auto& frame:frames){
            auto bitmap=decode(e,frame);
            if(!bitmap)throw std::runtime_error("Cannot decode clip frame.");mtmd_bitmap_set_mergeable(bitmap,true);bitmaps.push_back(bitmap);
            prompt+=mtmd_default_marker();
        }
        if(bitmaps.empty()||bitmaps.size()>8)throw std::runtime_error("Clip requires 1 to 8 ordered frames.");
        chunks=mtmd_input_chunks_init();mtmd_input_text text={prompt.c_str(),prompt.size(),true,true};
        std::vector<const mtmd_bitmap*> inputs(bitmaps.begin(),bitmaps.end());
        if(mtmd_tokenize(e->media,chunks,&text,inputs.data(),inputs.size())!=0)throw std::runtime_error("Cannot tokenize video clip.");
        llama_batch_ext_clear(e->batch);int position=0;
        for(size_t i=0;i<mtmd_input_chunks_size(chunks);++i){if(cancel&&*cancel)throw std::runtime_error("Clip embedding cancelled.");auto chunk=mtmd_input_chunks_get(chunks,i);
            if(mtmd_input_chunk_get_type(chunk)==MTMD_INPUT_CHUNK_TYPE_TEXT){size_t n=0;auto tokens=mtmd_input_chunk_get_tokens_text(chunk,&n);for(size_t j=0;j<n;++j)add_token(e,tokens[j],position);}
            else {auto rows=encode_rows(e,chunk);int width=llama_model_n_embd_inp(e->model);
                for(size_t j=0;j<mtmd_input_chunk_get_n_tokens(chunk);++j){llama_embd row={rows+j*width,1,size_t(width)};int index=llama_batch_ext_add_embd(e->batch,0,row);llama_pos pos[4]={position++,0,0,0};if(index<0||!llama_batch_ext_set_pos(e->batch,index,pos)||!llama_batch_ext_set_output_embd(e->batch,index,true))throw std::runtime_error("Clip input exceeds context.");}}
        }
        int n=finish(e,result,capacity,cancel);mtmd_input_chunks_free(chunks);for(auto b:bitmaps)mtmd_bitmap_free(b);return n;
    }catch(const std::exception& ex){if(chunks)mtmd_input_chunks_free(chunks);for(auto b:bitmaps)mtmd_bitmap_free(b);if(e)llama_batch_ext_clear(e->batch);error_out(error,ex.what());return -1;}
}
API int fi_embed_frames(engine* e,const char* paths,float* result,int capacity,volatile int32_t* cancel,char** error){
    std::vector<std::string> names;std::vector<encoded> frames;
    if(paths){std::string list(paths);size_t start=0;
        while(start<list.size()){auto end=list.find('\n',start);names.push_back(list.substr(start,end==std::string::npos?end:end-start));if(end==std::string::npos)break;start=end+1;}}
    for(auto& name:names)frames.push_back({name.c_str(),nullptr,0});
    return embed_frames(e,frames,result,capacity,cancel,error);
}
API int fi_embed_frame_buffers(engine* e,const unsigned char* const* data,const size_t* lengths,int count,float* result,int capacity,volatile int32_t* cancel,char** error){
    std::vector<encoded> frames;
    if(data&&lengths)for(int i=0;i<count;++i)frames.push_back({nullptr,data[i],lengths[i]});
    return embed_frames(e,frames,result,capacity,cancel,error);
}
