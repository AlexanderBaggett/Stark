fn main() {
    let mut values: Vec<i32> = Vec::new();
    let mut checksum = 0_i64;

    for i in 0_i32..4096 {
        values.push(i);
    }

    if values.len() != 4096 {
        std::process::exit(2);
    }

    while let Some(popped) = values.pop() {
        checksum += popped as i64;
    }

    if checksum != 8_386_560 {
        std::process::exit(4);
    }
}
