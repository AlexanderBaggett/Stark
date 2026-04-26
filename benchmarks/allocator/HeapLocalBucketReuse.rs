struct BoxValue {
    value: i64,
}

fn main() {
    let mut checksum = 0_i64;

    for chunk_start in (0_i32..20_000).step_by(128) {
        let chunk_end = (chunk_start + 128).min(20_000);
        let mut values = Vec::with_capacity((chunk_end - chunk_start) as usize);

        for i in chunk_start..chunk_end {
            values.push(Box::new(BoxValue { value: i as i64 }));
        }

        for value in values {
            checksum += value.value;
        }
    }

    if checksum != 199_990_000 {
        std::process::exit(1);
    }
}
