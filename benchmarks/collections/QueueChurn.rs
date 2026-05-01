use std::collections::VecDeque;

fn main() {
    let mut queue: VecDeque<i32> = VecDeque::new();
    let mut checksum = 0_i64;
    let mut next = 0_i32;

    for _ in 0..256 {
        for _ in 0..64 {
            queue.push_back(next);
            next += 1;
        }

        for _ in 0..32 {
            checksum += queue.pop_front().unwrap() as i64;
        }

        for _ in 0..32 {
            queue.push_back(next);
            next += 1;
        }

        for _ in 0..64 {
            checksum += queue.pop_front().unwrap() as i64;
        }
    }

    if !queue.is_empty() || next != 24_576 || checksum != 301_977_600 {
        std::process::exit(5);
    }
}
