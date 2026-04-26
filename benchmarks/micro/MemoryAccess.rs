fn main() {
    let seed = (std::process::id() % 31) as i64;
    let mut values = [
        1_i64, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16,
    ];
    let mut checksum = 0_i64;

    for i in 0_i32..200_000 {
        let index = (i % 16) as usize;
        values[index] = values[index] + index as i64 + seed;
        checksum += values[index];
    }

    if checksum <= 0 {
        std::process::exit(1);
    }
}
