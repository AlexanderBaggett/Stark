use std::collections::HashMap;

fn main() {
    let mut values = HashMap::with_capacity(2048);

    for i in 0_i32..2048 {
        values.insert(i, i * 3);
    }

    let mut checksum = 0_i64;
    for i in 0_i32..100_000 {
        let key = i % 2048;
        let Some(found) = values.get(&key) else {
            std::process::exit(2);
        };

        checksum += *found as i64;
    }

    if checksum != 306_154_512 {
        std::process::exit(3);
    }
}
