fn main() {
    let mut queue: Vec<i32> = Vec::new();
    let mut checksum = 0_i64;

    for i in 0_i32..4096 {
        queue.push(i);
    }

    if queue.len() != 4096 || queue.capacity() < 4096 {
        std::process::exit(2);
    }

    for value in &queue {
        checksum += *value as i64;
    }

    if checksum != 8_386_560 {
        std::process::exit(4);
    }
}
