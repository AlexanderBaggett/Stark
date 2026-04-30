fn sum_bytes(values: &[i8]) -> i64 {
    let mut checksum = 0_i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn sum_code_points(values: &[i32]) -> i64 {
    let mut checksum = 0_i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn main() {
    const ITERATIONS: usize = 10000;
    const BYTE_COUNT: usize = 32;
    const CODE_POINT_COUNT: usize = 32;
    const MOVE_COUNT: usize = 16;
    const MOVE_DESTINATION_START: usize = 8;

    let byte_source = vec![3_i8; BYTE_COUNT];
    let mut byte_destination = vec![0_i8; BYTE_COUNT];
    let code_point_source = vec![65_i32; CODE_POINT_COUNT];
    let mut code_point_destination = vec![0_i32; CODE_POINT_COUNT];
    let mut byte_move_buffer = [0_i8; BYTE_COUNT];
    let mut code_point_move_buffer = [0_i32; CODE_POINT_COUNT];
    let mut checksum = 0_i64;

    for iteration in 0..ITERATIONS {
        byte_destination.copy_from_slice(&byte_source);
        checksum += sum_bytes(&byte_destination);

        byte_destination.fill(((iteration % 17) + 1) as i8);
        checksum += sum_bytes(&byte_destination);

        code_point_destination.copy_from_slice(&code_point_source);
        checksum += sum_code_points(&code_point_destination);

        code_point_destination.fill(90 + (iteration % 11) as i32);
        checksum += sum_code_points(&code_point_destination);

        for index in 0..BYTE_COUNT {
            byte_move_buffer[index] = ((index + iteration) % 97) as i8;
            code_point_move_buffer[index] = 65 + ((index + iteration) % 17) as i32;
        }

        byte_move_buffer.copy_within(0..MOVE_COUNT, MOVE_DESTINATION_START);
        checksum += sum_bytes(&byte_move_buffer);

        code_point_move_buffer.copy_within(0..MOVE_COUNT, MOVE_DESTINATION_START);
        checksum += sum_code_points(&code_point_move_buffer);
    }

    if checksum != 93_749_676 {
        std::process::exit(1);
    }
}
