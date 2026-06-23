#include <stdlib.h>
#include <string.h>
#include <zlib.h>

typedef struct stark_zlib_stream {
    z_stream stream;
} stark_zlib_stream;

int stark_zlib_deflate_init(stark_zlib_stream **handle, int level)
{
    if (handle == NULL) {
        return Z_STREAM_ERROR;
    }

    *handle = NULL;

    stark_zlib_stream *wrapper = (stark_zlib_stream *)malloc(sizeof(stark_zlib_stream));
    if (wrapper == NULL) {
        return Z_MEM_ERROR;
    }

    memset(wrapper, 0, sizeof(stark_zlib_stream));

    int status = deflateInit(&wrapper->stream, level);
    if (status != Z_OK) {
        free(wrapper);
        return status;
    }

    *handle = wrapper;
    return Z_OK;
}

int stark_zlib_deflate_update(
    stark_zlib_stream *handle,
    const unsigned char *source,
    unsigned int source_length,
    unsigned char *destination,
    unsigned int destination_length,
    int flush,
    unsigned int *consumed,
    unsigned int *produced,
    int *finished)
{
    if (handle == NULL || destination == NULL || consumed == NULL || produced == NULL || finished == NULL) {
        return Z_STREAM_ERROR;
    }

    if (source_length != 0 && source == NULL) {
        return Z_STREAM_ERROR;
    }

    handle->stream.next_in = (Bytef *)source;
    handle->stream.avail_in = source_length;
    handle->stream.next_out = destination;
    handle->stream.avail_out = destination_length;

    int status = deflate(&handle->stream, flush);

    *consumed = source_length - handle->stream.avail_in;
    *produced = destination_length - handle->stream.avail_out;
    *finished = status == Z_STREAM_END;
    return status;
}

int stark_zlib_deflate_dispose(stark_zlib_stream *handle)
{
    if (handle == NULL) {
        return Z_OK;
    }

    int status = deflateEnd(&handle->stream);
    free(handle);
    return status;
}

int stark_zlib_inflate_init(stark_zlib_stream **handle)
{
    if (handle == NULL) {
        return Z_STREAM_ERROR;
    }

    *handle = NULL;

    stark_zlib_stream *wrapper = (stark_zlib_stream *)malloc(sizeof(stark_zlib_stream));
    if (wrapper == NULL) {
        return Z_MEM_ERROR;
    }

    memset(wrapper, 0, sizeof(stark_zlib_stream));

    int status = inflateInit(&wrapper->stream);
    if (status != Z_OK) {
        free(wrapper);
        return status;
    }

    *handle = wrapper;
    return Z_OK;
}

int stark_zlib_inflate_update(
    stark_zlib_stream *handle,
    const unsigned char *source,
    unsigned int source_length,
    unsigned char *destination,
    unsigned int destination_length,
    int flush,
    unsigned int *consumed,
    unsigned int *produced,
    int *finished)
{
    if (handle == NULL || destination == NULL || consumed == NULL || produced == NULL || finished == NULL) {
        return Z_STREAM_ERROR;
    }

    if (source_length != 0 && source == NULL) {
        return Z_STREAM_ERROR;
    }

    handle->stream.next_in = (Bytef *)source;
    handle->stream.avail_in = source_length;
    handle->stream.next_out = destination;
    handle->stream.avail_out = destination_length;

    int status = inflate(&handle->stream, flush);

    *consumed = source_length - handle->stream.avail_in;
    *produced = destination_length - handle->stream.avail_out;
    *finished = status == Z_STREAM_END;
    return status;
}

int stark_zlib_inflate_dispose(stark_zlib_stream *handle)
{
    if (handle == NULL) {
        return Z_OK;
    }

    int status = inflateEnd(&handle->stream);
    free(handle);
    return status;
}
