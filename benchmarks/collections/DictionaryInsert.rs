use std::collections::HashMap;

fn main() {
    let mut values = HashMap::with_capacity(8192);

    let mut checksum = 0_i64;
    for i in 0_i32..8192 {
        let value = i * 7;
        values.insert(i, value);
        checksum += value as i64;
    }

    let Some(found) = values.get(&8191) else {
        std::process::exit(3);
    };

    if values.len() != 8192 || *found != 57337 || checksum != 234_852_352 {
        std::process::exit(4);
    }
}
