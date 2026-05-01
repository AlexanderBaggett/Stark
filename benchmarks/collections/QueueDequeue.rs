use std::collections::VecDeque;

fn main() {
    let mut queue: VecDeque<i32> = VecDeque::new();
    let mut checksum = 0_i64;

    for i in 0_i32..32768 {
        queue.push_back(i);
    }

    while let Some(value) = queue.pop_front() {
        checksum += value as i64;
    }

    if checksum != 536_854_528 {
        std::process::exit(3);
    }
}
