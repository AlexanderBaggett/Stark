#define STB_TRUETYPE_IMPLEMENTATION
#include "native/stb/stb_truetype.h"

#include <limits.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

enum
{
    STARK_STBTT_OK = 0,
    STARK_STBTT_INVALID_ARGUMENT = -1,
    STARK_STBTT_OUT_OF_MEMORY = -2,
    STARK_STBTT_INVALID_FONT = -3,
    STARK_STBTT_DESTINATION_TOO_SMALL = -4,
    STARK_STBTT_PACK_FAILED = -5
};

typedef struct stark_stbtt_font
{
    unsigned char *data;
    int length;
    int font_index;
    int font_offset;
    stbtt_fontinfo info;
} stark_stbtt_font;

typedef struct stark_stbtt_packed_char
{
    uint16_t x0;
    uint16_t y0;
    uint16_t x1;
    uint16_t y1;
    float xoff;
    float yoff;
    float xadvance;
    float xoff2;
    float yoff2;
} stark_stbtt_packed_char;

static int stark_stbtt_checked_area(int width, int height, int stride, int *required)
{
    if (required == NULL)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    *required = 0;
    if (width <= 0 || height <= 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    if (stride == 0)
    {
        stride = width;
    }

    if (stride < width)
    {
        return STARK_STBTT_DESTINATION_TOO_SMALL;
    }

    if (height > INT32_MAX / stride)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    *required = height * stride;
    return STARK_STBTT_OK;
}

int stark_stbtt_font_create(
    const unsigned char *data,
    int length,
    int font_index,
    stark_stbtt_font **out_font)
{
    if (out_font == NULL)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    *out_font = NULL;
    if (data == NULL || length <= 0 || font_index < 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stark_stbtt_font *font = (stark_stbtt_font *)calloc(1, sizeof(stark_stbtt_font));
    if (font == NULL)
    {
        return STARK_STBTT_OUT_OF_MEMORY;
    }

    if (length > INT32_MAX - 64)
    {
        free(font);
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    font->data = (unsigned char *)calloc((size_t)length + 64u, 1u);
    if (font->data == NULL)
    {
        free(font);
        return STARK_STBTT_OUT_OF_MEMORY;
    }

    memcpy(font->data, data, (size_t)length);
    font->length = length;
    font->font_index = font_index;
    font->font_offset = stbtt_GetFontOffsetForIndex(font->data, font_index);
    if (font->font_offset < 0 || !stbtt_InitFont(&font->info, font->data, font->font_offset))
    {
        free(font->data);
        free(font);
        return STARK_STBTT_INVALID_FONT;
    }

    *out_font = font;
    return STARK_STBTT_OK;
}

void stark_stbtt_font_destroy(stark_stbtt_font *font)
{
    if (font == NULL)
    {
        return;
    }

    free(font->data);
    font->data = NULL;
    free(font);
}

int stark_stbtt_scale_for_pixel_height(stark_stbtt_font *font, float pixel_height, float *scale)
{
    if (font == NULL || scale == NULL || pixel_height <= 0.0f)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    *scale = stbtt_ScaleForPixelHeight(&font->info, pixel_height);
    return STARK_STBTT_OK;
}

int stark_stbtt_find_glyph(stark_stbtt_font *font, int codepoint, int *glyph)
{
    if (font == NULL || glyph == NULL || codepoint < 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    *glyph = stbtt_FindGlyphIndex(&font->info, codepoint);
    return STARK_STBTT_OK;
}

int stark_stbtt_get_font_vmetrics(
    stark_stbtt_font *font,
    int *ascent,
    int *descent,
    int *line_gap)
{
    if (font == NULL || ascent == NULL || descent == NULL || line_gap == NULL)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stbtt_GetFontVMetrics(&font->info, ascent, descent, line_gap);
    return STARK_STBTT_OK;
}

int stark_stbtt_get_glyph_hmetrics(
    stark_stbtt_font *font,
    int glyph,
    int *advance_width,
    int *left_side_bearing)
{
    if (font == NULL || advance_width == NULL || left_side_bearing == NULL || glyph <= 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stbtt_GetGlyphHMetrics(&font->info, glyph, advance_width, left_side_bearing);
    return STARK_STBTT_OK;
}

int stark_stbtt_get_glyph_bitmap_box(
    stark_stbtt_font *font,
    int glyph,
    float scale_x,
    float scale_y,
    int *x0,
    int *y0,
    int *x1,
    int *y1)
{
    if (font == NULL || x0 == NULL || y0 == NULL || x1 == NULL || y1 == NULL || glyph <= 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stbtt_GetGlyphBitmapBox(&font->info, glyph, scale_x, scale_y, x0, y0, x1, y1);
    return STARK_STBTT_OK;
}

int stark_stbtt_make_glyph_bitmap(
    stark_stbtt_font *font,
    unsigned char *output,
    int output_capacity,
    int width,
    int height,
    int stride,
    float scale_x,
    float scale_y,
    int glyph)
{
    int required = 0;
    int status = stark_stbtt_checked_area(width, height, stride, &required);
    if (status != STARK_STBTT_OK)
    {
        return status;
    }

    if (font == NULL || output == NULL || output_capacity < required || glyph <= 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stbtt_MakeGlyphBitmap(&font->info, output, width, height, stride, scale_x, scale_y, glyph);
    return STARK_STBTT_OK;
}

int stark_stbtt_pack_glyph_range(
    stark_stbtt_font *font,
    float pixel_height,
    int first_codepoint,
    int glyph_count,
    unsigned char *pixels,
    int pixel_capacity,
    int width,
    int height,
    int stride,
    int padding,
    unsigned int h_oversample,
    unsigned int v_oversample,
    stark_stbtt_packed_char *packed_chars,
    int packed_char_capacity)
{
    int required = 0;
    int status = stark_stbtt_checked_area(width, height, stride, &required);
    if (status != STARK_STBTT_OK)
    {
        return status;
    }

    if (font == NULL
        || pixels == NULL
        || packed_chars == NULL
        || pixel_capacity < required
        || glyph_count <= 0
        || packed_char_capacity < glyph_count
        || first_codepoint < 0
        || pixel_height <= 0.0f
        || padding < 0
        || h_oversample == 0
        || v_oversample == 0)
    {
        return STARK_STBTT_INVALID_ARGUMENT;
    }

    stbtt_packedchar *native_chars = (stbtt_packedchar *)calloc((size_t)glyph_count, sizeof(stbtt_packedchar));
    if (native_chars == NULL)
    {
        return STARK_STBTT_OUT_OF_MEMORY;
    }

    stbtt_pack_context context;
    if (!stbtt_PackBegin(&context, pixels, width, height, stride, padding, NULL))
    {
        free(native_chars);
        return STARK_STBTT_PACK_FAILED;
    }

    stbtt_PackSetOversampling(&context, h_oversample, v_oversample);
    stbtt_PackSetSkipMissingCodepoints(&context, 1);

    int packed = stbtt_PackFontRange(
        &context,
        font->data,
        font->font_index,
        pixel_height,
        first_codepoint,
        glyph_count,
        native_chars);
    stbtt_PackEnd(&context);

    if (!packed)
    {
        free(native_chars);
        return STARK_STBTT_PACK_FAILED;
    }

    for (int index = 0; index < glyph_count; index += 1)
    {
        packed_chars[index].x0 = native_chars[index].x0;
        packed_chars[index].y0 = native_chars[index].y0;
        packed_chars[index].x1 = native_chars[index].x1;
        packed_chars[index].y1 = native_chars[index].y1;
        packed_chars[index].xoff = native_chars[index].xoff;
        packed_chars[index].yoff = native_chars[index].yoff;
        packed_chars[index].xadvance = native_chars[index].xadvance;
        packed_chars[index].xoff2 = native_chars[index].xoff2;
        packed_chars[index].yoff2 = native_chars[index].yoff2;
    }

    free(native_chars);
    return STARK_STBTT_OK;
}
