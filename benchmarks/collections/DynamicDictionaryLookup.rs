fn dictionary_set(states: &mut [i32], keys: &mut [i32], values: &mut [i32], key: i32, value: i32) -> bool {
    let mut index = key as usize % states.len();
    for _ in 0..states.len() {
        if states[index] == 0 || keys[index] == key {
            states[index] = 1;
            keys[index] = key;
            values[index] = value;
            return true;
        }

        index = (index + 1) % states.len();
    }

    false
}

fn dictionary_get(states: &[i32], keys: &[i32], values: &[i32], key: i32) -> Option<i32> {
    let mut index = key as usize % states.len();
    for _ in 0..states.len() {
        if states[index] == 0 {
            return None;
        }

        if keys[index] == key {
            return Some(values[index]);
        }

        index = (index + 1) % states.len();
    }

    None
}

fn main() {
    const CAPACITY: usize = 4096;
    let mut states = vec![0_i32; CAPACITY];
    let mut keys = vec![0_i32; CAPACITY];
    let mut values = vec![0_i32; CAPACITY];

    for i in 0_i32..2048 {
        if !dictionary_set(&mut states, &mut keys, &mut values, i, i * 3) {
            std::process::exit(1);
        }
    }

    let mut checksum = 0_i64;
    for i in 0_i32..100_000 {
        let key = i % 2048;
        let Some(found) = dictionary_get(&states, &keys, &values, key) else {
            std::process::exit(2);
        };

        checksum += found as i64;
    }

    if checksum != 306_154_512 {
        std::process::exit(3);
    }
}
