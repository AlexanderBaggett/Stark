static mut COUNTER: i64 = 1;

#[inline(never)]
fn mix(value: i64) -> i64 {
    (value ^ (value >> 7)) + 17
}

fn accumulate(salt: i64) -> i64 {
    unsafe {
        COUNTER = (COUNTER + salt) % 1_000_000_007;
        let first = COUNTER;
        let mixed = mix(first);
        let second = COUNTER;
        mixed + first + second
    }
}

fn main() {
    unsafe {
        COUNTER = (std::process::id() as i64 % 31) + 1;
    }

    let mut checksum = 0_i64;

    for i in 0_i32..200_000 {
        checksum += accumulate((i % 97) as i64);
        checksum %= 1_000_000_007;
    }

    if checksum <= 0 {
        std::process::exit(1);
    }
}
