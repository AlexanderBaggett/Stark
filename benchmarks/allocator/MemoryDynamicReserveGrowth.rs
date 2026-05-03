fn append_fill<T: Copy>(values: &mut Vec<T>, value: T, count: usize) {
    values.reserve(count);
    for _ in 0..count {
        values.push(value);
    }
}

fn sum_bytes(values: &[i8]) -> i64 {
    let mut checksum = values.len() as i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn sum_code_points(values: &[i32]) -> i64 {
    let mut checksum = values.len() as i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn main() {
    const ITERATIONS: usize = 800;
    const CHUNKS: usize = 32;
    let byte_source: [i8; 16] = [
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16,
    ];
    let code_point_source: [i32; 8] = [
        65, 66, 67, 68,
        69, 70, 71, 72,
    ];
    let mut checksum = 0_i64;

    for _ in 0..ITERATIONS {
        let mut bytes: Vec<i8> = Vec::new();
        let mut code_points: Vec<i32> = Vec::new();

        for _ in 0..CHUNKS {
            bytes.reserve(byte_source.len());
            bytes.extend_from_slice(&byte_source);
            code_points.reserve(code_point_source.len());
            code_points.extend_from_slice(&code_point_source);
        }

        append_fill(&mut bytes, 7, 32);
        append_fill(&mut code_points, 90, 32);

        checksum += sum_bytes(&bytes);
        checksum += sum_code_points(&code_points);
    }

    if checksum != 20_659_200 {
        std::process::exit(1);
    }
}
