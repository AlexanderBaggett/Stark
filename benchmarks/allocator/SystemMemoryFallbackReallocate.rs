fn main() {
    let mut checksum = 0_i64;

    for i in 0_i32..12_000 {
        let mut allocation = vec![0_u8; 16];
        allocation[0] = i as u8;

        allocation.resize(32, 0);
        if allocation[0] != i as u8 || allocation.len() != 32 {
            std::process::exit(1);
        }

        checksum += i as i64;
    }

    if checksum != 71_994_000 {
        std::process::exit(1);
    }
}
