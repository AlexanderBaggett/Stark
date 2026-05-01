use std::collections::HashMap;

fn main() {
    let mut values = HashMap::with_capacity(4096);

    for i in 0_i32..2048 {
        values.insert(i, i);
    }

    let mut checksum = 0_i64;
    for i in 0_i32..4096 {
        let key = i % 2048;
        match i % 4 {
            0 => {
                values.insert(key, i);
            }
            1 => {
                if let Some(found) = values.get(&key) {
                    checksum += *found as i64;
                }
            }
            2 => {
                values.remove(&key);
            }
            _ => {
                values.insert(key, i * 3);
            }
        }
    }

    let mut final_sum = 0_i64;
    for key in 0_i32..2048 {
        if let Some(found) = values.get(&key) {
            final_sum += *found as i64;
        }
    }

    if values.len() != 1536 || checksum != 1_047_552 || final_sum != 6_815_744 {
        std::process::exit(5);
    }
}
