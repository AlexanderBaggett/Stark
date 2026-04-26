struct BoxValue {
    value: i64,
}

fn main() {
    const TOTAL: i32 = 20_000;
    const BATCH: usize = 128;

    let mut boxes: [Option<Box<BoxValue>>; BATCH] = std::array::from_fn(|_| None);
    let mut checksum = 0_i64;

    for chunk_start in (0_i32..TOTAL).step_by(BATCH) {
        let chunk_end = (chunk_start + BATCH as i32).min(TOTAL);
        let count = (chunk_end - chunk_start) as usize;

        for index in 0..count {
            boxes[index] = Some(Box::new(BoxValue {
                value: (chunk_start + index as i32) as i64,
            }));
        }

        for slot in boxes.iter_mut().take(count) {
            let value = slot.take().expect("allocated batch slot");
            checksum += value.value;
        }
    }

    if checksum != 199_990_000 {
        std::process::exit(1);
    }
}
