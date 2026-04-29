use std::collections::LinkedList;

fn prebuild(values: &mut LinkedList<i32>) -> bool {
    for i in 0_i32..4096 {
        values.push_back(i);
    }

    values.len() == 4096
}

fn main() {
    let mut values: LinkedList<i32> = LinkedList::new();
    let mut checksum = 0_i64;

    if !prebuild(&mut values) {
        std::process::exit(1);
    }

    while let Some(value) = values.pop_back() {
        checksum += value as i64;
    }

    if checksum != 8_386_560 {
        std::process::exit(3);
    }
}
