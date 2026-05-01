use std::collections::HashMap;

fn main() {
    let mut values = HashMap::with_capacity(4096);

    for i in 0_i32..4096 {
        values.insert(i, i);
    }

    let mut checksum = 0_i64;
    for i in 0_i32..4096 {
        if values.remove(&i).is_none() {
            std::process::exit(3);
        }

        checksum += i as i64;
    }

    if !values.is_empty() || checksum != 8_386_560 {
        std::process::exit(4);
    }
}
