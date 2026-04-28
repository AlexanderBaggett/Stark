const ITERATIONS: i32 = 50_000;
const CAPACITY: usize = 512;

fn main() {
    let source = std::env::args_os().next().unwrap_or_default();
    let bytes = source.as_encoded_bytes();
    if bytes.is_empty() || bytes.len() > CAPACITY {
        std::process::exit(1);
    }

    let mut unicode = [0_i32; CAPACITY];
    let mut checksum = 0_i64;

    for _ in 0..ITERATIONS {
        for (index, unit) in bytes.iter().copied().enumerate() {
            if (unit & 0x80) != 0 {
                std::process::exit(2);
            }

            unicode[index] = i32::from(unit);
        }

        checksum += bytes.len() as i64;
        for value in &unicode[..bytes.len()] {
            checksum += i64::from(*value);
        }
    }

    if checksum <= 0 {
        std::process::exit(3);
    }
}
