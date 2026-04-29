use std::collections::LinkedList;

fn main() {
    let mut values: LinkedList<i32> = LinkedList::new();

    for i in 0_i32..4096 {
        values.push_back(i);
    }

    if values.len() != 4096 {
        std::process::exit(2);
    }

    values.clear();
    if !values.is_empty() {
        std::process::exit(3);
    }
}
