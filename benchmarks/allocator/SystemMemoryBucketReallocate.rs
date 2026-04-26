fn main() {
    let mut checksum = 0_i64;

    for i in 0_i32..20_000 {
        let mut allocation = vec![0_u8; 16];
        allocation[0] = i as u8;

        allocation.truncate(12);
        allocation.shrink_to_fit();
        if allocation[0] != i as u8 {
            std::process::exit(1);
        }

        allocation.truncate(8);
        allocation.shrink_to_fit();
        if allocation[0] != i as u8 {
            std::process::exit(1);
        }

        checksum += i as i64;
    }

    if checksum != 199_990_000 {
        std::process::exit(1);
    }
}
