use std::collections::HashMap;

fn main() {
    let mut values = HashMap::with_capacity(4096);

    for i in 0_i32..4096 {
        values.insert(i, i);
    }

    for i in 0_i32..4096 {
        values.insert(i, i * 5);
    }

    let mut checksum = 0_i64;
    for i in 0_i32..4096 {
        let Some(found) = values.get(&i) else {
            std::process::exit(4);
        };

        checksum += *found as i64;
    }

    if checksum != 41_932_800 {
        std::process::exit(5);
    }
}
