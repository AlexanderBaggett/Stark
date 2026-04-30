struct FixedByteBuffer512 {
    storage: [i8; 512],
    read_position: usize,
    write_position: usize,
}

impl FixedByteBuffer512 {
    fn new() -> Self {
        Self {
            storage: [0; 512],
            read_position: 0,
            write_position: 0,
        }
    }

    fn readable(&self) -> usize {
        self.write_position.saturating_sub(self.read_position)
    }

    fn writable(&self) -> usize {
        512_usize.saturating_sub(self.write_position)
    }

    fn clear(&mut self) {
        self.read_position = 0;
        self.write_position = 0;
    }

    fn write_slice(&mut self, source: &[i8]) -> bool {
        if source.len() > self.writable() {
            return false;
        }

        let end = self.write_position + source.len();
        self.storage[self.write_position..end].copy_from_slice(source);
        self.write_position = end;
        true
    }

    fn write_fill(&mut self, value: i8, count: usize) -> bool {
        if count > self.writable() {
            return false;
        }

        let end = self.write_position + count;
        self.storage[self.write_position..end].fill(value);
        self.write_position = end;
        true
    }

    fn advance_read(&mut self, count: usize) {
        let available = self.readable();
        if count >= available {
            self.clear();
            return;
        }

        self.read_position += count;
    }

    fn compact(&mut self) {
        let available = self.readable();
        if self.read_position == 0 {
            return;
        }

        if available == 0 {
            self.clear();
            return;
        }

        self.storage.copy_within(self.read_position..self.write_position, 0);
        self.read_position = 0;
        self.write_position = available;
    }
}

fn sum_bytes(values: &[i8]) -> i64 {
    let mut checksum = values.len() as i64;
    for value in values {
        checksum += *value as i64;
    }

    checksum
}

fn main() {
    const ITERATIONS: usize = 6000;
    let source: [i8; 32] = [
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
        1, 2, 3, 4, 5, 6, 7, 8,
        9, 10, 11, 12, 13, 14, 15, 16,
    ];
    let mut buffer = FixedByteBuffer512::new();
    let mut checksum = 0_i64;

    for _ in 0..ITERATIONS {
        buffer.clear();

        if !buffer.write_slice(&source) || !buffer.write_fill(3, 96) {
            std::process::exit(1);
        }

        checksum += sum_bytes(&buffer.storage[buffer.read_position..buffer.write_position]);
        buffer.advance_read(48);
        buffer.compact();

        if !buffer.write_slice(&source) {
            std::process::exit(1);
        }

        checksum += sum_bytes(&buffer.storage[buffer.read_position..buffer.write_position]);
    }

    if checksum != 7_872_000 {
        std::process::exit(1);
    }
}
