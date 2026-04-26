fn main() {
    let source = b"alpha/beta.txt";
    let mut unicode = [0_i32; 32];
    let mut checksum = 0_i64;

    for _ in 0_i32..200_000 {
        for (index, unit) in source.iter().copied().enumerate() {
            if (unit & 0x80) != 0 {
                std::process::exit(1);
            }

            unicode[index] = i32::from(unit);
        }

        checksum += source.len() as i64;
        checksum += i64::from(unicode[0]);
        checksum += i64::from(unicode[source.len() - 1]);
    }

    if checksum != 45_400_000 {
        std::process::exit(2);
    }
}
