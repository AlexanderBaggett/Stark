#include <stdint.h>
#include <stdlib.h>

typedef struct IntNode {
    int32_t value;
    struct IntNode *previous;
    struct IntNode *next;
} IntNode;

typedef struct {
    IntNode *head;
    IntNode *tail;
    size_t count;
} IntLinkedList;

static void linked_list_clear(IntLinkedList *list) {
    IntNode *current = list->head;
    while (current != NULL) {
        IntNode *next = current->next;
        free(current);
        current = next;
    }

    list->head = NULL;
    list->tail = NULL;
    list->count = 0;
}

static int linked_list_push_back(IntLinkedList *list, int32_t value) {
    IntNode *node = (IntNode *)malloc(sizeof(IntNode));
    if (node == NULL) {
        return 0;
    }

    node->value = value;
    node->previous = list->tail;
    node->next = NULL;

    if (list->tail != NULL) {
        list->tail->next = node;
    }
    else {
        list->head = node;
    }

    list->tail = node;
    list->count += 1;
    return 1;
}

int main(void) {
    IntLinkedList values = {0};

    for (int32_t i = 0; i < 4096; i += 1) {
        if (!linked_list_push_back(&values, i)) {
            linked_list_clear(&values);
            return 1;
        }
    }

    if (values.count != 4096) {
        linked_list_clear(&values);
        return 2;
    }

    linked_list_clear(&values);
    return values.count == 0 ? 0 : 3;
}
