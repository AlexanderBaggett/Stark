#[inline(never)]
fn score(ptr: *mut i64, seed: i64) -> i32 {
    if !ptr.is_null() {
        if ptr.is_null() {
            return 1;
        }

        ((seed & 7) + 2) as i32
    } else {
        0
    }
}

fn main() {
    let mut total = std::process::id() as i64;
    let mut slot = total;
    let ptr = &mut slot as *mut i64;

    for i in 0_i32..200_000 {
        total += score(ptr, total + i as i64) as i64;
        total %= 1_000_000_007;
    }

    if total <= 0 {
        std::process::exit(1);
    }
}
