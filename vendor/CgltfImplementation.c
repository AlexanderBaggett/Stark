#define CGLTF_IMPLEMENTATION
#include "native/cgltf/cgltf.h"

#include <stdint.h>
#include <stdlib.h>
#include <string.h>

enum
{
    STARK_CGLTF_INVALID_ARGUMENT = -1,
    STARK_CGLTF_OUT_OF_RANGE = -2,
    STARK_CGLTF_DESTINATION_TOO_SMALL = -3
};

enum
{
    STARK_CGLTF_NAME_MESH = 1,
    STARK_CGLTF_NAME_MATERIAL = 2,
    STARK_CGLTF_NAME_BUFFER = 3,
    STARK_CGLTF_NAME_BUFFER_URI = 4,
    STARK_CGLTF_NAME_NODE = 5,
    STARK_CGLTF_NAME_SCENE = 6,
    STARK_CGLTF_NAME_ANIMATION = 7
};

typedef struct stark_cgltf_document
{
    cgltf_data *data;
    unsigned char *owned_input;
} stark_cgltf_document;

typedef struct stark_cgltf_counts
{
    uint64_t meshes;
    uint64_t materials;
    uint64_t buffers;
    uint64_t buffer_views;
    uint64_t accessors;
    uint64_t images;
    uint64_t textures;
    uint64_t samplers;
    uint64_t skins;
    uint64_t cameras;
    uint64_t lights;
    uint64_t nodes;
    uint64_t scenes;
    uint64_t animations;
} stark_cgltf_counts;

typedef struct stark_cgltf_mesh_info
{
    uint64_t name_bytes;
    uint64_t primitives;
    uint64_t weights;
    uint64_t target_names;
} stark_cgltf_mesh_info;

typedef struct stark_cgltf_material_info
{
    uint64_t name_bytes;
    int32_t has_pbr_metallic_roughness;
    int32_t has_pbr_specular_glossiness;
    int32_t alpha_mode;
    int32_t double_sided;
    int32_t unlit;
} stark_cgltf_material_info;

typedef struct stark_cgltf_buffer_info
{
    uint64_t name_bytes;
    uint64_t uri_bytes;
    uint64_t size_bytes;
    int32_t is_loaded;
} stark_cgltf_buffer_info;

typedef struct stark_cgltf_node_info
{
    uint64_t name_bytes;
    uint64_t children;
    uint64_t weights;
    uint64_t mesh_index;
    uint64_t camera_index;
    uint64_t skin_index;
    int32_t has_mesh;
    int32_t has_camera;
    int32_t has_skin;
    int32_t has_translation;
    int32_t has_rotation;
    int32_t has_scale;
    int32_t has_matrix;
} stark_cgltf_node_info;

typedef struct stark_cgltf_scene_info
{
    uint64_t name_bytes;
    uint64_t nodes;
} stark_cgltf_scene_info;

typedef struct stark_cgltf_animation_info
{
    uint64_t name_bytes;
    uint64_t samplers;
    uint64_t channels;
} stark_cgltf_animation_info;

typedef struct stark_cgltf_accessor_info
{
    uint64_t name_bytes;
    uint64_t count;
    uint64_t stride;
    uint64_t buffer_view_index;
    int32_t component_type;
    int32_t type;
    int32_t normalized;
    int32_t has_buffer_view;
    int32_t is_sparse;
} stark_cgltf_accessor_info;

typedef struct stark_cgltf_primitive_info
{
    uint64_t attributes;
    uint64_t targets;
    uint64_t indices_accessor_index;
    uint64_t material_index;
    int32_t type;
    int32_t has_indices;
    int32_t has_material;
} stark_cgltf_primitive_info;

static uint64_t stark_cgltf_strlen64(const char *text)
{
    return text == NULL ? 0 : (uint64_t)strlen(text);
}

static int stark_cgltf_check_document(const stark_cgltf_document *document)
{
    return document != NULL && document->data != NULL;
}

static uint64_t stark_cgltf_index_or_none(const void *base, cgltf_size count, size_t element_size, const void *item)
{
    if (base == NULL || item == NULL || element_size == 0)
    {
        return UINT64_MAX;
    }

    const unsigned char *start = (const unsigned char *)base;
    const unsigned char *target = (const unsigned char *)item;
    const unsigned char *end = start + ((size_t)count * element_size);
    if (target < start || target >= end)
    {
        return UINT64_MAX;
    }

    size_t byte_offset = (size_t)(target - start);
    if (byte_offset % element_size != 0)
    {
        return UINT64_MAX;
    }

    return (uint64_t)(byte_offset / element_size);
}

static int stark_cgltf_finish_parse(
    cgltf_data *data,
    unsigned char *owned_input,
    int validate,
    stark_cgltf_document **document)
{
    if (validate)
    {
        cgltf_result validate_result = cgltf_validate(data);
        if (validate_result != cgltf_result_success)
        {
            cgltf_free(data);
            free(owned_input);
            return (int)validate_result;
        }
    }

    stark_cgltf_document *handle = (stark_cgltf_document *)calloc(1, sizeof(*handle));
    if (handle == NULL)
    {
        cgltf_free(data);
        free(owned_input);
        return (int)cgltf_result_out_of_memory;
    }

    handle->data = data;
    handle->owned_input = owned_input;
    *document = handle;
    return (int)cgltf_result_success;
}

int stark_cgltf_parse_memory(
    const unsigned char *input,
    size_t input_size,
    int validate,
    stark_cgltf_document **document)
{
    if (document == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    *document = NULL;
    if (input == NULL || input_size == 0)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    unsigned char *owned_input = (unsigned char *)malloc(input_size);
    if (owned_input == NULL)
    {
        return (int)cgltf_result_out_of_memory;
    }

    memcpy(owned_input, input, input_size);

    cgltf_options options;
    memset(&options, 0, sizeof(options));
    cgltf_data *data = NULL;
    cgltf_result result = cgltf_parse(&options, owned_input, input_size, &data);
    if (result != cgltf_result_success)
    {
        free(owned_input);
        return (int)result;
    }

    return stark_cgltf_finish_parse(data, owned_input, validate, document);
}

int stark_cgltf_parse_file(
    const char *path,
    int load_buffers,
    int validate,
    stark_cgltf_document **document)
{
    if (document == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    *document = NULL;
    if (path == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    cgltf_options options;
    memset(&options, 0, sizeof(options));
    cgltf_data *data = NULL;
    cgltf_result result = cgltf_parse_file(&options, path, &data);
    if (result != cgltf_result_success)
    {
        return (int)result;
    }

    if (load_buffers)
    {
        result = cgltf_load_buffers(&options, data, path);
        if (result != cgltf_result_success)
        {
            cgltf_free(data);
            return (int)result;
        }
    }

    return stark_cgltf_finish_parse(data, NULL, validate, document);
}

void stark_cgltf_free(stark_cgltf_document *document)
{
    if (document == NULL)
    {
        return;
    }

    cgltf_free(document->data);
    free(document->owned_input);
    free(document);
}

int stark_cgltf_validate(stark_cgltf_document *document)
{
    if (!stark_cgltf_check_document(document))
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    return (int)cgltf_validate(document->data);
}

int stark_cgltf_get_counts(stark_cgltf_document *document, stark_cgltf_counts *counts)
{
    if (!stark_cgltf_check_document(document) || counts == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    cgltf_data *data = document->data;
    counts->meshes = (uint64_t)data->meshes_count;
    counts->materials = (uint64_t)data->materials_count;
    counts->buffers = (uint64_t)data->buffers_count;
    counts->buffer_views = (uint64_t)data->buffer_views_count;
    counts->accessors = (uint64_t)data->accessors_count;
    counts->images = (uint64_t)data->images_count;
    counts->textures = (uint64_t)data->textures_count;
    counts->samplers = (uint64_t)data->samplers_count;
    counts->skins = (uint64_t)data->skins_count;
    counts->cameras = (uint64_t)data->cameras_count;
    counts->lights = (uint64_t)data->lights_count;
    counts->nodes = (uint64_t)data->nodes_count;
    counts->scenes = (uint64_t)data->scenes_count;
    counts->animations = (uint64_t)data->animations_count;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_mesh_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_mesh_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->meshes_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_mesh *mesh = &document->data->meshes[index];
    info->name_bytes = stark_cgltf_strlen64(mesh->name);
    info->primitives = (uint64_t)mesh->primitives_count;
    info->weights = (uint64_t)mesh->weights_count;
    info->target_names = (uint64_t)mesh->target_names_count;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_material_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_material_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->materials_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_material *material = &document->data->materials[index];
    info->name_bytes = stark_cgltf_strlen64(material->name);
    info->has_pbr_metallic_roughness = material->has_pbr_metallic_roughness ? 1 : 0;
    info->has_pbr_specular_glossiness = material->has_pbr_specular_glossiness ? 1 : 0;
    info->alpha_mode = (int32_t)material->alpha_mode;
    info->double_sided = material->double_sided ? 1 : 0;
    info->unlit = material->unlit ? 1 : 0;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_buffer_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_buffer_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->buffers_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_buffer *buffer = &document->data->buffers[index];
    info->name_bytes = stark_cgltf_strlen64(buffer->name);
    info->uri_bytes = stark_cgltf_strlen64(buffer->uri);
    info->size_bytes = (uint64_t)buffer->size;
    info->is_loaded = buffer->data != NULL ? 1 : 0;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_node_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_node_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->nodes_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_node *node = &document->data->nodes[index];
    info->name_bytes = stark_cgltf_strlen64(node->name);
    info->children = (uint64_t)node->children_count;
    info->weights = (uint64_t)node->weights_count;
    info->mesh_index = stark_cgltf_index_or_none(document->data->meshes, document->data->meshes_count, sizeof(cgltf_mesh), node->mesh);
    info->camera_index = stark_cgltf_index_or_none(document->data->cameras, document->data->cameras_count, sizeof(cgltf_camera), node->camera);
    info->skin_index = stark_cgltf_index_or_none(document->data->skins, document->data->skins_count, sizeof(cgltf_skin), node->skin);
    info->has_mesh = node->mesh != NULL ? 1 : 0;
    info->has_camera = node->camera != NULL ? 1 : 0;
    info->has_skin = node->skin != NULL ? 1 : 0;
    info->has_translation = node->has_translation ? 1 : 0;
    info->has_rotation = node->has_rotation ? 1 : 0;
    info->has_scale = node->has_scale ? 1 : 0;
    info->has_matrix = node->has_matrix ? 1 : 0;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_scene_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_scene_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->scenes_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_scene *scene = &document->data->scenes[index];
    info->name_bytes = stark_cgltf_strlen64(scene->name);
    info->nodes = (uint64_t)scene->nodes_count;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_animation_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_animation_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->animations_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_animation *animation = &document->data->animations[index];
    info->name_bytes = stark_cgltf_strlen64(animation->name);
    info->samplers = (uint64_t)animation->samplers_count;
    info->channels = (uint64_t)animation->channels_count;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_accessor_info(stark_cgltf_document *document, uint64_t index, stark_cgltf_accessor_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (index >= document->data->accessors_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_accessor *accessor = &document->data->accessors[index];
    info->name_bytes = stark_cgltf_strlen64(accessor->name);
    info->count = (uint64_t)accessor->count;
    info->stride = (uint64_t)accessor->stride;
    info->buffer_view_index = stark_cgltf_index_or_none(document->data->buffer_views, document->data->buffer_views_count, sizeof(cgltf_buffer_view), accessor->buffer_view);
    info->component_type = (int32_t)accessor->component_type;
    info->type = (int32_t)accessor->type;
    info->normalized = accessor->normalized ? 1 : 0;
    info->has_buffer_view = accessor->buffer_view != NULL ? 1 : 0;
    info->is_sparse = accessor->is_sparse ? 1 : 0;
    return (int)cgltf_result_success;
}

int stark_cgltf_get_primitive_info(stark_cgltf_document *document, uint64_t mesh_index, uint64_t primitive_index, stark_cgltf_primitive_info *info)
{
    if (!stark_cgltf_check_document(document) || info == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    if (mesh_index >= document->data->meshes_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_mesh *mesh = &document->data->meshes[mesh_index];
    if (primitive_index >= mesh->primitives_count)
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    cgltf_primitive *primitive = &mesh->primitives[primitive_index];
    info->attributes = (uint64_t)primitive->attributes_count;
    info->targets = (uint64_t)primitive->targets_count;
    info->indices_accessor_index = stark_cgltf_index_or_none(document->data->accessors, document->data->accessors_count, sizeof(cgltf_accessor), primitive->indices);
    info->material_index = stark_cgltf_index_or_none(document->data->materials, document->data->materials_count, sizeof(cgltf_material), primitive->material);
    info->type = (int32_t)primitive->type;
    info->has_indices = primitive->indices != NULL ? 1 : 0;
    info->has_material = primitive->material != NULL ? 1 : 0;
    return (int)cgltf_result_success;
}

static const char *stark_cgltf_name_for_kind(stark_cgltf_document *document, int kind, uint64_t index)
{
    cgltf_data *data = document->data;
    switch (kind)
    {
        case STARK_CGLTF_NAME_MESH:
            return index < data->meshes_count ? data->meshes[index].name : NULL;
        case STARK_CGLTF_NAME_MATERIAL:
            return index < data->materials_count ? data->materials[index].name : NULL;
        case STARK_CGLTF_NAME_BUFFER:
            return index < data->buffers_count ? data->buffers[index].name : NULL;
        case STARK_CGLTF_NAME_BUFFER_URI:
            return index < data->buffers_count ? data->buffers[index].uri : NULL;
        case STARK_CGLTF_NAME_NODE:
            return index < data->nodes_count ? data->nodes[index].name : NULL;
        case STARK_CGLTF_NAME_SCENE:
            return index < data->scenes_count ? data->scenes[index].name : NULL;
        case STARK_CGLTF_NAME_ANIMATION:
            return index < data->animations_count ? data->animations[index].name : NULL;
        default:
            return NULL;
    }
}

static int stark_cgltf_kind_index_in_range(stark_cgltf_document *document, int kind, uint64_t index)
{
    cgltf_data *data = document->data;
    switch (kind)
    {
        case STARK_CGLTF_NAME_MESH:
            return index < data->meshes_count;
        case STARK_CGLTF_NAME_MATERIAL:
            return index < data->materials_count;
        case STARK_CGLTF_NAME_BUFFER:
        case STARK_CGLTF_NAME_BUFFER_URI:
            return index < data->buffers_count;
        case STARK_CGLTF_NAME_NODE:
            return index < data->nodes_count;
        case STARK_CGLTF_NAME_SCENE:
            return index < data->scenes_count;
        case STARK_CGLTF_NAME_ANIMATION:
            return index < data->animations_count;
        default:
            return 0;
    }
}

int stark_cgltf_copy_name(
    stark_cgltf_document *document,
    int kind,
    uint64_t index,
    char *destination,
    size_t destination_capacity,
    uint64_t *bytes_copied)
{
    if (!stark_cgltf_check_document(document) || bytes_copied == NULL)
    {
        return STARK_CGLTF_INVALID_ARGUMENT;
    }

    *bytes_copied = 0;
    if (!stark_cgltf_kind_index_in_range(document, kind, index))
    {
        return STARK_CGLTF_OUT_OF_RANGE;
    }

    const char *name = stark_cgltf_name_for_kind(document, kind, index);
    uint64_t length = stark_cgltf_strlen64(name);
    if (length == 0)
    {
        return (int)cgltf_result_success;
    }

    if (destination == NULL || destination_capacity < length)
    {
        return STARK_CGLTF_DESTINATION_TOO_SMALL;
    }

    memcpy(destination, name, (size_t)length);
    *bytes_copied = length;
    return (int)cgltf_result_success;
}
