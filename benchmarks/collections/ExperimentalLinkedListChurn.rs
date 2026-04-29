use std::collections::LinkedList;

fn main() {
    let mut values: LinkedList<i32> = LinkedList::new();
    let mut checksum = 0_i64;

    for i in 0_i32..4096 {
        values.push_back(i);

        if let Some(value) = values.pop_front() {
            checksum += value as i64;
        }
        else {
            std::process::exit(2);
        }
    }

    if !values.is_empty() {
        std::process::exit(3);
    }

    if checksum != 8_386_560 {
        std::process::exit(4);
    }
}
