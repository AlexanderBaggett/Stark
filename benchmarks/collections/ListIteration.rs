fn main() {
    let mut values: Vec<i32> = Vec::new();

    for i in 0_i32..4096 {
        values.push(i);
    }

    let mut checksum = 0_i64;
    for value in &values {
        checksum += *value as i64;
    }

    if checksum != 8_386_560 {
        std::process::exit(2);
    }
}
