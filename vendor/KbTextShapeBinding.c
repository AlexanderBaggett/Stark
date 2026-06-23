#include <hb.h>
#include <hb-ot.h>
#include <unicode/ubrk.h>
#include <unicode/utext.h>
#include <unicode/utypes.h>

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

enum
{
    STARK_KB_OK = 0,
    STARK_KB_INVALID_ARGUMENT = -1,
    STARK_KB_OUT_OF_MEMORY = -2,
    STARK_KB_ICU_ERROR = -3,
    STARK_KB_DESTINATION_TOO_SMALL = -4,
    STARK_KB_INVALID_FONT = -5,
    STARK_KB_SHAPE_FAILED = -6
};

typedef struct stark_kb_segmenter
{
    UBreakIterator *iterator;
} stark_kb_segmenter;

typedef struct stark_kb_font
{
    hb_blob_t *blob;
    hb_face_t *face;
    hb_font_t *font;
    uint32_t units_per_em;
} stark_kb_font;

typedef struct stark_kb_boundary
{
    uint32_t start;
    uint32_t end;
    int32_t rule_status;
} stark_kb_boundary;

typedef struct stark_kb_glyph
{
    uint32_t glyph_id;
    uint32_t cluster;
    int32_t x_advance;
    int32_t y_advance;
    int32_t x_offset;
    int32_t y_offset;
} stark_kb_glyph;

static int stark_kb_check_output_counts(int32_t *written, int32_t *required)
{
    if (written == NULL || required == NULL)
    {
        return STARK_KB_INVALID_ARGUMENT;
    }

    *written = 0;
    *required = 0;
    return STARK_KB_OK;
}

int stark_kb_segmenter_create(int32_t kind, stark_kb_segmenter **segmenter)
{
    UErrorCode status = U_ZERO_ERROR;
    UBreakIterator *iterator = NULL;
    stark_kb_segmenter *created = NULL;

    if (segmenter == NULL || kind < UBRK_CHARACTER || kind > UBRK_LINE)
    {
        return STARK_KB_INVALID_ARGUMENT;
    }

    *segmenter = NULL;
    iterator = ubrk_open((UBreakIteratorType)kind, "und", NULL, 0, &status);
    if (U_FAILURE(status) || iterator == NULL)
    {
        return STARK_KB_ICU_ERROR;
    }

    created = (stark_kb_segmenter *)calloc(1, sizeof(*created));
    if (created == NULL)
    {
        ubrk_close(iterator);
        return STARK_KB_OUT_OF_MEMORY;
    }

    created->iterator = iterator;
    *segmenter = created;
    return STARK_KB_OK;
}

void stark_kb_segmenter_destroy(stark_kb_segmenter *segmenter)
{
    if (segmenter == NULL)
    {
        return;
    }

    if (segmenter->iterator != NULL)
    {
        ubrk_close(segmenter->iterator);
        segmenter->iterator = NULL;
    }

    free(segmenter);
}

int stark_kb_segment_utf8(
    stark_kb_segmenter *segmenter,
    const char *text,
    int32_t text_length,
    stark_kb_boundary *boundaries,
    int32_t boundary_capacity,
    int32_t *written,
    int32_t *required)
{
    UErrorCode status = U_ZERO_ERROR;
    UText *utext = NULL;
    int32_t start = 0;
    int32_t end = 0;
    int32_t count = 0;
    int result = stark_kb_check_output_counts(written, required);

    if (result != STARK_KB_OK)
    {
        return result;
    }

    if (segmenter == NULL
        || segmenter->iterator == NULL
        || text_length < 0
        || boundary_capacity < 0
        || (text_length > 0 && text == NULL)
        || (boundary_capacity > 0 && boundaries == NULL))
    {
        return STARK_KB_INVALID_ARGUMENT;
    }

    if (text_length == 0)
    {
        return STARK_KB_OK;
    }

    utext = utext_openUTF8(NULL, text, text_length, &status);
    if (U_FAILURE(status) || utext == NULL)
    {
        return STARK_KB_ICU_ERROR;
    }

    status = U_ZERO_ERROR;
    ubrk_setUText(segmenter->iterator, utext, &status);
    if (U_FAILURE(status))
    {
        utext_close(utext);
        return STARK_KB_ICU_ERROR;
    }

    start = ubrk_first(segmenter->iterator);
    while ((end = ubrk_next(segmenter->iterator)) != UBRK_DONE)
    {
        if (count < boundary_capacity)
        {
            boundaries[count].start = (uint32_t)start;
            boundaries[count].end = (uint32_t)end;
            boundaries[count].rule_status = ubrk_getRuleStatus(segmenter->iterator);
        }

        start = end;
        ++count;
    }

    utext_close(utext);
    *required = count;
    *written = count < boundary_capacity ? count : boundary_capacity;
    return count <= boundary_capacity ? STARK_KB_OK : STARK_KB_DESTINATION_TOO_SMALL;
}

int stark_kb_font_create(
    const uint8_t *font_data,
    int32_t font_length,
    uint32_t face_index,
    stark_kb_font **font)
{
    uint8_t *owned_data = NULL;
    stark_kb_font *created = NULL;
    unsigned int units_per_em = 0;

    if (font == NULL || font_length <= 0 || font_data == NULL)
    {
        return STARK_KB_INVALID_ARGUMENT;
    }

    *font = NULL;
    owned_data = (uint8_t *)malloc((size_t)font_length);
    if (owned_data == NULL)
    {
        return STARK_KB_OUT_OF_MEMORY;
    }

    memcpy(owned_data, font_data, (size_t)font_length);
    created = (stark_kb_font *)calloc(1, sizeof(*created));
    if (created == NULL)
    {
        free(owned_data);
        return STARK_KB_OUT_OF_MEMORY;
    }

    created->blob = hb_blob_create_or_fail(
        (const char *)owned_data,
        (unsigned int)font_length,
        HB_MEMORY_MODE_READONLY,
        owned_data,
        free);
    if (created->blob == NULL)
    {
        free(owned_data);
        free(created);
        return STARK_KB_INVALID_FONT;
    }

    created->face = hb_face_create_or_fail(created->blob, face_index);
    if (created->face == NULL)
    {
        hb_blob_destroy(created->blob);
        free(created);
        return STARK_KB_INVALID_FONT;
    }

    created->font = hb_font_create(created->face);
    if (created->font == NULL)
    {
        hb_face_destroy(created->face);
        hb_blob_destroy(created->blob);
        free(created);
        return STARK_KB_OUT_OF_MEMORY;
    }

    hb_ot_font_set_funcs(created->font);
    units_per_em = hb_face_get_upem(created->face);
    if (units_per_em == 0)
    {
        units_per_em = 1000;
    }

    hb_font_set_scale(created->font, (int)units_per_em, (int)units_per_em);
    hb_font_make_immutable(created->font);
    created->units_per_em = units_per_em;
    *font = created;
    return STARK_KB_OK;
}

void stark_kb_font_destroy(stark_kb_font *font)
{
    if (font == NULL)
    {
        return;
    }

    if (font->font != NULL)
    {
        hb_font_destroy(font->font);
        font->font = NULL;
    }

    if (font->face != NULL)
    {
        hb_face_destroy(font->face);
        font->face = NULL;
    }

    if (font->blob != NULL)
    {
        hb_blob_destroy(font->blob);
        font->blob = NULL;
    }

    free(font);
}

uint32_t stark_kb_font_units_per_em(stark_kb_font *font)
{
    if (font == NULL)
    {
        return 0;
    }

    return font->units_per_em;
}

int stark_kb_shape_utf8(
    stark_kb_font *font,
    const char *text,
    int32_t text_length,
    stark_kb_glyph *glyphs,
    int32_t glyph_capacity,
    int32_t *written,
    int32_t *required)
{
    hb_buffer_t *buffer = NULL;
    unsigned int glyph_count = 0;
    hb_glyph_info_t *infos = NULL;
    hb_glyph_position_t *positions = NULL;
    unsigned int index = 0;
    unsigned int write_count = 0;
    int result = stark_kb_check_output_counts(written, required);

    if (result != STARK_KB_OK)
    {
        return result;
    }

    if (font == NULL
        || font->font == NULL
        || text_length < 0
        || glyph_capacity < 0
        || (text_length > 0 && text == NULL)
        || (glyph_capacity > 0 && glyphs == NULL))
    {
        return STARK_KB_INVALID_ARGUMENT;
    }

    if (text_length == 0)
    {
        return STARK_KB_OK;
    }

    buffer = hb_buffer_create();
    if (buffer == NULL)
    {
        return STARK_KB_OUT_OF_MEMORY;
    }

    hb_buffer_pre_allocate(buffer, (unsigned int)glyph_capacity);
    if (!hb_buffer_allocation_successful(buffer))
    {
        hb_buffer_destroy(buffer);
        return STARK_KB_OUT_OF_MEMORY;
    }

    hb_buffer_add_utf8(buffer, text, text_length, 0, text_length);
    hb_buffer_guess_segment_properties(buffer);
    hb_shape(font->font, buffer, NULL, 0);

    glyph_count = hb_buffer_get_length(buffer);
    infos = hb_buffer_get_glyph_infos(buffer, NULL);
    positions = hb_buffer_get_glyph_positions(buffer, NULL);
    if (infos == NULL || positions == NULL)
    {
        hb_buffer_destroy(buffer);
        return STARK_KB_SHAPE_FAILED;
    }

    write_count = glyph_count < (unsigned int)glyph_capacity ? glyph_count : (unsigned int)glyph_capacity;
    for (index = 0; index < write_count; ++index)
    {
        glyphs[index].glyph_id = infos[index].codepoint;
        glyphs[index].cluster = infos[index].cluster;
        glyphs[index].x_advance = positions[index].x_advance;
        glyphs[index].y_advance = positions[index].y_advance;
        glyphs[index].x_offset = positions[index].x_offset;
        glyphs[index].y_offset = positions[index].y_offset;
    }

    *required = (int32_t)glyph_count;
    *written = (int32_t)write_count;
    hb_buffer_destroy(buffer);
    return glyph_count <= (unsigned int)glyph_capacity ? STARK_KB_OK : STARK_KB_DESTINATION_TOO_SMALL;
}
