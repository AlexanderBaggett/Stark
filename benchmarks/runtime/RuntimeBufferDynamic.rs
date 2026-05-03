struct DynamicByteBuffer {
    data: Vec<i8>,
    read_position: usize,
}

impl DynamicByteBuffer {
    fn new() -> Self {
        Self {
            data: Vec::new(),
            read_position: 0,
        }
    }

    fn readable(&self) -> usize {
        self.data.len().saturating_sub(self.read_position)
    }

    fn write_slice(&mut self, source: &[i8]) {
        self.data.reserve(source.len());
        self.data.extend_from_slice(source);
    }

    fn write_fill(&mut self, value: i8, count: usize) {
        self.data.reserve(count);
        for _ in 0..count {
            self.data.push(value);
        }
    }

    fn advance_read(&mut self, count: usize) {
        let available = self.readable();
        if count >= available {
            self.data.clear();
            self.read_position = 0;
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
            self.data.clear();
            self.read_position = 0;
            return;
        }

        self.data.copy_within(self.read_position.., 0);
        self.data.truncate(available);
        self.read_position = 0;
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
    const ITERATIONS: usize = 1000;
    const CHUNKS: usize = 32;
    let source: [i8; 16] = [
        1, 2, 3, 4,
        5, 6, 7, 8,
        9, 10, 11, 12,
        13, 14, 15, 16,
    ];
    let mut checksum = 0_i64;

    for _ in 0..ITERATIONS {
        let mut buffer = DynamicByteBuffer::new();

        for _ in 0..CHUNKS {
            buffer.write_slice(&source);
        }

        buffer.advance_read(128);
        buffer.compact();
        buffer.write_fill(5, 64);

        checksum += sum_bytes(&buffer.data[buffer.read_position..]);
    }

    if checksum != 4_032_000 {
        std::process::exit(1);
    }
}
