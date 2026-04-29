use std::collections::VecDeque;

fn main() {
    let mut queue: VecDeque<i32> = VecDeque::new();
    let mut checksum = 0_i64;

    for i in 0_i32..4096 {
        queue.push_back(i);
    }

    if queue.len() != 4096 {
        std::process::exit(2);
    }

    while let Some(value) = queue.pop_front() {
        checksum += value as i64;
    }

    if checksum != 8_386_560 {
        std::process::exit(4);
    }
}
