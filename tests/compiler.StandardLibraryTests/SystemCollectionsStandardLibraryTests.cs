using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsStandardLibraryTests : StandardLibraryTestSuite
{
    private const string CollectionsGrowthMoveDropProgram = """
        import System.Collections
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsPowerOfTwo(u64[0 2 ** 63 - 1] value) {
            if (value == 0) {
                return false;
            }

            stack u64[0 2 ** 63 - 1] mask = (u64[0 2 ** 63 - 1])(value - 1);
            return (value & mask) == 0;
        }

        fn bool ConsumeList(List<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected) {
            return values.Count() == expected && values.Capacity() >= expected;
        }

        fn bool ConsumeStack(Stack<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected) {
            return values.Count() == expected && values.Peek() == 79;
        }

        fn bool ConsumeQueue(Queue<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected) {
            return values.Count() == expected && values.Peek() == 0;
        }

        fn bool ConsumeLinkedList(LinkedList<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected) {
            return values.Count() == expected;
        }

        fn bool ConsumeDictionary(Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected) {
            stack u32[0 2 ** 31 - 1] key = 17;
            stack mut u32[0 2 ** 31 - 1] found = 0;
            return values.Count() == expected
                && IsPowerOfTwo(values.Capacity())
                && values.ContainsKey(key)
                && values.TryGet(key, found)
                && found == 34;
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut List<u32[0 2 ** 31 - 1]> list = new();
            for willexit (stack mut u8[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(list.Push(i))) {
                    return 1;
                }
            }

            if (!ConsumeList(list, 96)) {
                return 2;
            }

            stack mut Stack<u32[0 2 ** 31 - 1]> stackValues = new();
            for willexit (stack mut u8[0 80] i = 0; i < 80; i += 1) {
                if (!Ok(stackValues.Push(i))) {
                    return 3;
                }
            }

            if (!ConsumeStack(stackValues, 80)) {
                return 4;
            }

            stack mut Queue<u32[0 2 ** 31 - 1]> queue = new();
            for willexit (stack mut u8[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(queue.Enqueue(i))) {
                    return 5;
                }
            }

            if (!ConsumeQueue(queue, 96)) {
                return 6;
            }

            stack mut LinkedList<u32[0 2 ** 31 - 1]> linked = new();
            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1) {
                if (!Ok(linked.AddLast(i))) {
                    return 7;
                }
            }

            if (!ConsumeLinkedList(linked, 48)) {
                return 8;
            }

            stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity())) {
                return 9;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1) {
                stack u32[0 2 ** 31 - 1] key = i;
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i * 2);
                if (!Ok(dictionary.Set(key, value))) {
                    return 9;
                }

                if (!IsPowerOfTwo(dictionary.Capacity())) {
                    return 9;
                }

                if (i == 4 && dictionary.Capacity() < 16) {
                    return 9;
                }
            }

            if (!ConsumeDictionary(dictionary, 64)) {
                return 10;
            }

            return 0;
        }
        """;

    private const string PromotedListParityProgram = """
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLarge(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return false;
                case MemoryStatus.Err(var error):
                    return error == MemoryError.TooLarge;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0))) {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i))) {
                    return 2;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count()) {
                return 3;
            }

            stable.GetMut(10) = 111;
            experimental.GetMut(10) = 111;
            stable.AsMutableSlice()[11] = 222;
            experimental.AsMutableSlice()[11] = 222;

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i]) {
                    return 4;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (experimental.Count() > 0) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue)) {
                    return 5;
                }

                if (stableValue != experimentalValue) {
                    return 6;
                }

                checksum += (i64[min max])stableValue;
            }

            if (stable.Count() != 0 || experimental.Count() != 0 || checksum != 8440) {
                return 7;
            }

            if (!TooLarge(stable.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || !TooLarge(experimental.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1)))) {
                return 8;
            }

            {
                stack mut System.Collections.List<Resource> stableDrops = new();
                if (!Ok(stableDrops.Push(new Resource() { Value = 1 })) || !Ok(stableDrops.Push(new Resource() { Value = 2 }))) {
                    return 9;
                }

                stableDrops.Clear();
                if (DropCounter != 3) {
                    return 10;
                }
            }

            if (DropCounter != 3) {
                return 11;
            }

            {
                stack mut System.Collections.List<Resource> experimentalDrops = new();
                if (!Ok(experimentalDrops.Push(new Resource() { Value = 4 })) || !Ok(experimentalDrops.Push(new Resource() { Value = 5 }))) {
                    return 12;
                }

                experimentalDrops.Clear();
                if (DropCounter != 12) {
                    return 13;
                }
            }

            if (DropCounter != 12) {
                return 14;
            }

            {
                stack mut System.Collections.List<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Push(new Resource() { Value = 6 })) || !Ok(scopedDrops.Push(new Resource() { Value = 7 }))) {
                    return 15;
                }
            }

            if (DropCounter != 25) {
                return 16;
            }

            return 0;
        }
        """;

    private const string PromotedStackParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> experimental = new();

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i))) {
                    return 1;
                }

                if (stable.Peek() != experimental.Peek()) {
                    return 2;
                }
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty()) {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue)) {
                    return 4;
                }

                if (stableValue != experimentalValue) {
                    return 5;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 8128) {
                return 6;
            }

            {
                stack mut System.Collections.Stack<Resource> stableDrops = new();
                if (!Ok(stableDrops.Push(new Resource() { Value = 1 })) || !Ok(stableDrops.Push(new Resource() { Value = 2 }))) {
                    return 7;
                }

                stableDrops.Clear();
                if (DropCounter != 3) {
                    return 8;
                }
            }

            if (DropCounter != 3) {
                return 9;
            }

            {
                stack mut System.Collections.Stack<Resource> experimentalDrops = new();
                if (!Ok(experimentalDrops.Push(new Resource() { Value = 4 })) || !Ok(experimentalDrops.Push(new Resource() { Value = 5 }))) {
                    return 10;
                }

                experimentalDrops.Clear();
                if (DropCounter != 12) {
                    return 11;
                }
            }

            if (DropCounter != 12) {
                return 12;
            }

            {
                stack mut System.Collections.Stack<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Push(new Resource() { Value = 6 })) || !Ok(scopedDrops.Push(new Resource() { Value = 7 }))) {
                    return 13;
                }
            }

            if (DropCounter != 25) {
                return 14;
            }

            return 0;
        }
        """;

    private const string PromotedQueueParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLarge(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return false;
                case MemoryStatus.Err(var error):
                    return error == MemoryError.TooLarge;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0))) {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i))) {
                    return 2;
                }

                if (stable.Peek() != experimental.Peek()) {
                    return 3;
                }
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty()) {
                return 4;
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue)) {
                    return 5;
                }

                if (stableValue != experimentalValue) {
                    return 6;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 8128) {
                return 7;
            }

            if (!TooLarge(stable.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1))) || !TooLarge(experimental.Reserve((u64[0 2 ** 63 - 1])(2 ** 63 - 1)))) {
                return 8;
            }

            {
                stack mut System.Collections.Queue<Resource> stableDrops = new();
                if (!Ok(stableDrops.Enqueue(new Resource() { Value = 1 })) || !Ok(stableDrops.Enqueue(new Resource() { Value = 2 }))) {
                    return 9;
                }

                stableDrops.Clear();
                if (DropCounter != 3) {
                    return 10;
                }
            }

            if (DropCounter != 3) {
                return 11;
            }

            {
                stack mut System.Collections.Queue<Resource> experimentalDrops = new();
                if (!Ok(experimentalDrops.Enqueue(new Resource() { Value = 4 })) || !Ok(experimentalDrops.Enqueue(new Resource() { Value = 5 }))) {
                    return 12;
                }

                experimentalDrops.Clear();
                if (DropCounter != 12) {
                    return 13;
                }
            }

            if (DropCounter != 12) {
                return 14;
            }

            {
                stack mut System.Collections.Queue<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Enqueue(new Resource() { Value = 6 })) || !Ok(scopedDrops.Enqueue(new Resource() { Value = 7 }))) {
                    return 15;
                }
            }

            if (DropCounter != 25) {
                return 16;
            }

            return 0;
        }
        """;

    private const string PromotedRingQueueCandidateProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ring = new();

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return 1;
                }
            }

            if (ring.Peek() != 0) {
                return 14;
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue)) {
                    return 2;
                }

                if (stableValue != ringValue) {
                    return 3;
                }

                checksum += (i64[min max])stableValue;
            }

            for willexit (stack mut u8[0 128] i = 64; i < 128; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return 4;
                }
            }

            if (stable.Count() != ring.Count() || ring.Capacity() < ring.Count()) {
                return 5;
            }

            if (ring.Peek() != 32) {
                return 15;
            }

            while willexit (!ring.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue)) {
                    return 6;
                }

                if (stableValue != ringValue) {
                    return 7;
                }

                checksum += (i64[min max])stableValue;
            }

            if (!stable.IsEmpty() || !ring.IsEmpty() || checksum != 8128) {
                return 8;
            }

            {
                stack mut System.Collections.RingQueue<Resource> ringDrops = new();
                if (!Ok(ringDrops.Enqueue(new Resource() { Value = 1 })) || !Ok(ringDrops.Enqueue(new Resource() { Value = 2 }))) {
                    return 9;
                }

                ringDrops.Clear();
                if (DropCounter != 3) {
                    return 10;
                }
            }

            if (DropCounter != 3) {
                return 11;
            }

            {
                stack mut System.Collections.RingQueue<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.Enqueue(new Resource() { Value = 4 })) || !Ok(scopedDrops.Enqueue(new Resource() { Value = 5 }))) {
                    return 12;
                }
            }

            if (DropCounter != 12) {
                return 13;
            }

            return 0;
        }
        """;

    private const string PromotedLinkedListParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> experimental = new();

            if (!Ok(stable.ReserveNodes(4)) || !Ok(experimental.ReserveNodes(4))) {
                return 1;
            }

            if (stable.Count() != 0 || experimental.Count() != 0 || !stable.IsEmpty() || !experimental.IsEmpty()) {
                return 2;
            }

            if (!Ok(stable.AddLast(1)) || !Ok(experimental.AddLast(1))) {
                return 3;
            }

            if (!Ok(stable.AddLast(2)) || !Ok(experimental.AddLast(2))) {
                return 4;
            }

            if (!Ok(stable.AddFirst(0)) || !Ok(experimental.AddFirst(0))) {
                return 5;
            }

            if (stable.Count() != experimental.Count() || stable.IsEmpty() != experimental.IsEmpty()) {
                return 6;
            }

            stack mut u32[0 2 ** 31 - 1] stableValue = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue)) {
                return 7;
            }

            if (stableValue != 0 || experimentalValue != 0) {
                return 8;
            }

            if (!stable.TryRemoveLast(stableValue) || !experimental.TryRemoveLast(experimentalValue)) {
                return 9;
            }

            if (stableValue != 2 || experimentalValue != 2) {
                return 10;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue)) {
                return 11;
            }

            if (stableValue != 1 || experimentalValue != 1 || !stable.IsEmpty() || !experimental.IsEmpty()) {
                return 12;
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1) {
                if (!Ok(stable.AddLast(i)) || !Ok(experimental.AddLast(i))) {
                    return 13;
                }

                if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue)) {
                    return 14;
                }

                if (stableValue != experimentalValue) {
                    return 15;
                }

                checksum += (i64[min max])experimentalValue;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty() || checksum != 2016) {
                return 16;
            }

            {
                stack mut System.Collections.LinkedList<Resource> drops = new();
                if (!Ok(drops.AddLast(new Resource() { Value = 1 })) || !Ok(drops.AddFirst(new Resource() { Value = 2 }))) {
                    return 17;
                }

                drops.Clear();
                if (DropCounter != 3) {
                    return 18;
                }
            }

            if (DropCounter != 3) {
                return 19;
            }

            {
                stack mut System.Collections.LinkedList<Resource> scopedDrops = new();
                if (!Ok(scopedDrops.AddLast(new Resource() { Value = 4 })) || !Ok(scopedDrops.AddLast(new Resource() { Value = 5 }))) {
                    return 20;
                }
            }

            if (DropCounter != 12) {
                return 21;
            }

            return 0;
        }
        """;

    private const string PromotedDictionaryProgram = """
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsPowerOfTwo(u64[0 2 ** 63 - 1] value) {
            if (value == 0) {
                return false;
            }

            stack u64[0 2 ** 63 - 1] mask = (u64[0 2 ** 63 - 1])(value - 1);
            return (value & mask) == 0;
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity())) {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                stack u32[0 2 ** 31 - 1] key = i;
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i * 5);
                if (!Ok(dictionary.Set(key, value)) || !IsPowerOfTwo(dictionary.Capacity())) {
                    return 2;
                }
            }

            if (dictionary.Count() != 128 || dictionary.IsEmpty()) {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            stack mut u32[0 2 ** 31 - 1] found = 0;
            stack u32[0 2 ** 31 - 1] refKey = 7;
            stack u64[0 2 ** 63 - 1] refIndex = dictionary.FindIndex(refKey);
            if (!dictionary.ContainsIndex(refIndex) || dictionary.GetAtIndex(refIndex) != 35) {
                return 25;
            }

            dictionary.GetMutAtIndex(refIndex) = 36;
            if (dictionary.GetAtIndex(refIndex) != 36) {
                return 26;
            }

            dictionary.GetMutAtIndex(refIndex) = 35;
            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1) {
                stack u32[0 2 ** 31 - 1] key = i;
                if (!dictionary.ContainsKey(key) || !dictionary.TryGet(key, found) || found != (u32[0 2 ** 31 - 1])(i * 5)) {
                    return 4;
                }

                checksum += (i64[min max])found;
            }

            if (checksum != 40640) {
                return 5;
            }

            stack u32[0 2 ** 31 - 1] updateKey = 64;
            if (!Ok(dictionary.Set(updateKey, 999)) || !dictionary.TryGet(updateKey, found) || found != 999 || dictionary.Count() != 128) {
                return 6;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1) {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                if (!dictionary.Remove(key)) {
                    return 7;
                }
            }

            if (dictionary.Count() != 64) {
                return 8;
            }

            stack u32[0 2 ** 31 - 1] removedKey = 65;
            if (!dictionary.TryRemove(removedKey, found) || found != 325 || dictionary.ContainsKey(removedKey) || dictionary.Count() != 63) {
                return 9;
            }

            stack u32[0 2 ** 31 - 1] tombstoneKey = 4096;
            if (!Ok(dictionary.Set(tombstoneKey, 12345)) || !dictionary.TryGet(tombstoneKey, found) || found != 12345) {
                return 10;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> clustered = new();
                stack u32[0 2 ** 31 - 1] clusterKeyOne = 1;
                stack u32[0 2 ** 31 - 1] clusterKeyTwo = 9;
                stack u32[0 2 ** 31 - 1] clusterKeyThree = 17;
                stack u32[0 2 ** 31 - 1] clusterKeyFour = 25;
                if (!Ok(clustered.Reserve(4))
                    || !Ok(clustered.Set(clusterKeyOne, 10))
                    || !Ok(clustered.Set(clusterKeyTwo, 90))
                    || !Ok(clustered.Set(clusterKeyThree, 170))) {
                    return 27;
                }

                if (!clustered.Remove(clusterKeyOne)
                    || clustered.ContainsKey(clusterKeyOne)
                    || !clustered.ContainsIndex(1)
                    || !clustered.TryGet(clusterKeyTwo, found)
                    || found != 90
                    || !clustered.TryGet(clusterKeyThree, found)
                    || found != 170
                    || clustered.Count() != 2) {
                    return 28;
                }

                if (!Ok(clustered.Set(clusterKeyFour, 250))
                    || !clustered.TryGet(clusterKeyFour, found)
                    || found != 250
                    || clustered.Count() != 3) {
                    return 29;
                }
            }

            dictionary.Clear();
            if (!dictionary.IsEmpty() || dictionary.Count() != 0 || dictionary.ContainsKey(tombstoneKey)) {
                return 11;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> drops = new();
                stack u32[0 2 ** 31 - 1] keyOne = 1;
                stack u32[0 2 ** 31 - 1] keyTwo = 2;
                stack u32[0 2 ** 31 - 1] keyThree = 3;
                stack u32[0 2 ** 31 - 1] keyFour = 4;
                if (!Ok(drops.Set(keyOne, new Resource() { Value = 10 }))
                    || !Ok(drops.Set(keyTwo, new Resource() { Value = 20 }))
                    || !Ok(drops.Set(keyOne, new Resource() { Value = 30 }))) {
                    return 12;
                }

                if (DropCounter != 10) {
                    return 13;
                }

                if (!drops.Remove(keyTwo)) {
                    return 14;
                }

                if (DropCounter != 30) {
                    return 15;
                }

                {
                    stack DictionaryRemoveResult<Resource> removedResult = drops.RemoveMove(keyOne);
                    switch (removedResult) {
                        case DictionaryRemoveResult<Resource>.Missing:
                            return 16;
                        case DictionaryRemoveResult<Resource>.Removed(var removed):
                            if (DropCounter != 30 || removed.Value != 30) {
                                return 17;
                            }
                    }
                }

                if (DropCounter != 60) {
                    return 18;
                }

                if (!Ok(drops.Set(keyThree, new Resource() { Value = 40 }))
                    || !Ok(drops.Set(keyFour, new Resource() { Value = 50 }))
                    || !Ok(drops.Reserve(64))) {
                    return 19;
                }

                if (DropCounter != 60) {
                    return 20;
                }

                drops.Clear();
                if (DropCounter != 150 || drops.Count() != 0) {
                    return 21;
                }
            }

            if (DropCounter != 150) {
                return 22;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> scopedDrops = new();
                stack u32[0 2 ** 31 - 1] scopedKey = 7;
                if (!Ok(scopedDrops.Set(scopedKey, new Resource() { Value = 60 }))) {
                    return 23;
                }
            }

            if (DropCounter != 210) {
                return 24;
            }

            return 0;
        }
        """;

    private const string PromotedCollectionsCrossFamilyParityProgram = """
        import System.Collections
        import System.Collections
        import System.Memory
        module App

        static mut i32[min max] DropCounter = 0;

        fn bool Ok(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLarge(MemoryStatus status) {
            switch (status) {
                case MemoryStatus.Ok:
                    return false;
                case MemoryStatus.Err(var error):
                    return error == MemoryError.TooLarge;
            }
        }

        fn void Bump(i32[min max] value) {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource {
            i32[min max] Value;

            drop {
                Bump(self.Value);
            }
        }

        fn bool CheckListParity() {
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(3)) || !Ok(experimental.Reserve(3))) {
                return false;
            }

            for willexit (stack mut u8[0 40] i = 0; i < 40; i += 1) {
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])((i * 3) + 1);
                if (!Ok(stable.Push(value)) || !Ok(experimental.Push(value))) {
                    return false;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count()) {
                return false;
            }

            stable.GetMut(5) = 55;
            experimental.GetMut(5) = 55;
            stable.AsMutableSlice()[7] = 77;
            experimental.AsMutableSlice()[7] = 77;

            for willexit (stack mut u8[0 40] i = 0; i < 40; i += 1) {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i]) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 20] i = 0; i < 20; i += 1) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            if (stable.Count() != 20 || experimental.Count() != 20 || checksum != 1790) {
                return false;
            }

            stable.Clear();
            experimental.Clear();
            stack u64[0 2 ** 63 - 1] impossible = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckStackParity() {
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(2)) || !Ok(experimental.Reserve(2))) {
                return false;
            }

            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1) {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i)) || stable.Peek() != experimental.Peek()) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            stack u64[0 2 ** 63 - 1] impossible = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && checksum == 496
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckQueueParity() {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(4)) || !Ok(experimental.Reserve(4))) {
                return false;
            }

            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i)) || stable.Peek() != experimental.Peek()) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 16] i = 0; i < 16; i += 1) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            for willexit (stack mut u8[0 72] i = 48; i < 72; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i))) {
                    return false;
                }
            }

            while willexit (!experimental.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            stack u64[0 2 ** 63 - 1] impossible = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && checksum == 2556
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckRingQueueParity() {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ring = new();
            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 12] i = 0; i < 12; i += 1) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue) {
                    return false;
                }

                checksum += (i64[min max])ringValue;
            }

            for willexit (stack mut u8[0 64] i = 32; i < 64; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return false;
                }
            }

            while willexit (!ring.IsEmpty()) {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue) {
                    return false;
                }

                checksum += (i64[min max])ringValue;
            }

            stack u64[0 2 ** 63 - 1] impossible = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
            return stable.IsEmpty()
                && ring.IsEmpty()
                && checksum == 2016
                && TooLarge(ring.Reserve(impossible));
        }

        fn bool CheckLinkedListParity() {
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.ReserveNodes(3)) || !Ok(experimental.ReserveNodes(3))) {
                return false;
            }

            if (!Ok(stable.AddLast(10)) || !Ok(experimental.AddLast(10))
                || !Ok(stable.AddLast(20)) || !Ok(experimental.AddLast(20))
                || !Ok(stable.AddFirst(5)) || !Ok(experimental.AddFirst(5))
                || !Ok(stable.AddLast(30)) || !Ok(experimental.AddLast(30))) {
                return false;
            }

            stack mut u32[0 2 ** 31 - 1] stableValue = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 5 || experimentalValue != 5) {
                return false;
            }

            if (!stable.TryRemoveLast(stableValue) || !experimental.TryRemoveLast(experimentalValue) || stableValue != 30 || experimentalValue != 30) {
                return false;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 10 || experimentalValue != 10) {
                return false;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 20 || experimentalValue != 20) {
                return false;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty()) {
                return false;
            }

            for willexit (stack mut u8[0 24] i = 0; i < 24; i += 1) {
                if (!Ok(stable.AddLast(i)) || !Ok(experimental.AddLast(i))) {
                    return false;
                }
            }

            stable.Clear();
            experimental.Clear();
            return stable.Count() == 0 && experimental.Count() == 0;
        }

        fn bool CheckDictionaryParity() {
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(5)) || !Ok(experimental.Reserve(5))) {
                return false;
            }

            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1) {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i + 100);
                if (!Ok(stable.Set(key, value)) || !Ok(experimental.Set(key, value))) {
                    return false;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count()) {
                return false;
            }

            stack mut i64[min max] checksum = 0;
            stack mut u32[0 2 ** 31 - 1] stableFound = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalFound = 0;
            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1) {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                if (!stable.TryGet(key, stableFound) || !experimental.TryGet(key, experimentalFound) || stableFound != experimentalFound) {
                    return false;
                }

                checksum += (i64[min max])experimentalFound;
            }

            if (checksum != 5928) {
                return false;
            }

            stack u32[0 2 ** 31 - 1] updateKey = 20;
            if (!Ok(stable.Set(updateKey, 999)) || !Ok(experimental.Set(updateKey, 999))
                || stable.Count() != experimental.Count()
                || !stable.TryGet(updateKey, stableFound)
                || !experimental.TryGet(updateKey, experimentalFound)
                || stableFound != experimentalFound
                || experimentalFound != 999) {
                return false;
            }

            for willexit (stack mut u8[0 12] i = 0; i < 12; i += 1) {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 4);
                if (!stable.Remove(key) || !experimental.Remove(key) || stable.ContainsKey(key) || experimental.ContainsKey(key)) {
                    return false;
                }
            }

            stack u32[0 2 ** 31 - 1] tombstoneKey = 777;
            if (stable.Count() != 36
                || experimental.Count() != 36
                || !Ok(stable.Set(tombstoneKey, 12345))
                || !Ok(experimental.Set(tombstoneKey, 12345))
                || !stable.TryGet(tombstoneKey, stableFound)
                || !experimental.TryGet(tombstoneKey, experimentalFound)
                || stableFound != experimentalFound) {
                return false;
            }

            stable.Clear();
            experimental.Clear();
            stack u64[0 2 ** 63 - 1] impossible = (u64[0 2 ** 63 - 1])(2 ** 63 - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckOwnedValueCleanup() {
            DropCounter = 0;
            {
                stack mut System.Collections.List<Resource> values = new();
                if (!Ok(values.Push(new Resource() { Value = 1 })) || !Ok(values.Push(new Resource() { Value = 2 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 3) {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> values = new();
                if (!Ok(values.Push(new Resource() { Value = 3 })) || !Ok(values.Push(new Resource() { Value = 4 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 10) {
                return false;
            }

            {
                stack mut System.Collections.Stack<Resource> values = new();
                if (!Ok(values.Push(new Resource() { Value = 5 })) || !Ok(values.Push(new Resource() { Value = 6 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 21) {
                return false;
            }

            {
                stack mut System.Collections.Stack<Resource> values = new();
                if (!Ok(values.Push(new Resource() { Value = 7 })) || !Ok(values.Push(new Resource() { Value = 8 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 36) {
                return false;
            }

            {
                stack mut System.Collections.Queue<Resource> values = new();
                if (!Ok(values.Enqueue(new Resource() { Value = 9 })) || !Ok(values.Enqueue(new Resource() { Value = 10 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 55) {
                return false;
            }

            {
                stack mut System.Collections.Queue<Resource> values = new();
                if (!Ok(values.Enqueue(new Resource() { Value = 11 })) || !Ok(values.Enqueue(new Resource() { Value = 12 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 78) {
                return false;
            }

            {
                stack mut System.Collections.LinkedList<Resource> values = new();
                if (!Ok(values.AddLast(new Resource() { Value = 13 })) || !Ok(values.AddFirst(new Resource() { Value = 14 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 105) {
                return false;
            }

            {
                stack mut System.Collections.LinkedList<Resource> values = new();
                if (!Ok(values.AddLast(new Resource() { Value = 15 })) || !Ok(values.AddFirst(new Resource() { Value = 16 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 136) {
                return false;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> values = new();
                stack u32[0 2 ** 31 - 1] one = 1;
                stack u32[0 2 ** 31 - 1] two = 2;
                if (!Ok(values.Set(one, new Resource() { Value = 17 })) || !Ok(values.Set(two, new Resource() { Value = 18 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 171) {
                return false;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> values = new();
                stack u32[0 2 ** 31 - 1] three = 3;
                stack u32[0 2 ** 31 - 1] four = 4;
                if (!Ok(values.Set(three, new Resource() { Value = 19 })) || !Ok(values.Set(four, new Resource() { Value = 20 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 210) {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> stableScoped = new();
                if (!Ok(stableScoped.Push(new Resource() { Value = 21 })) || !Ok(stableScoped.Push(new Resource() { Value = 22 }))) {
                    return false;
                }
            }

            if (DropCounter != 253) {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> experimentalScoped = new();
                if (!Ok(experimentalScoped.Push(new Resource() { Value = 23 })) || !Ok(experimentalScoped.Push(new Resource() { Value = 24 }))) {
                    return false;
                }
            }

            return DropCounter == 300;
        }

        export unsafe ffi fn i32[min max] main() {
            if (!CheckListParity()) {
                return 1;
            }

            if (!CheckStackParity()) {
                return 2;
            }

            if (!CheckQueueParity()) {
                return 3;
            }

            if (!CheckRingQueueParity()) {
                return 4;
            }

            if (!CheckLinkedListParity()) {
                return 5;
            }

            if (!CheckDictionaryParity()) {
                return 6;
            }

            if (!CheckOwnedValueCleanup()) {
                return 7;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceCollectionsSupportOwnedAllocatorBackedSurface()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibCollectionsSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UseCollections() {
                    stack mut List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Push(10))) {
                        return false;
                    }
                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12) {
                        return false;
                    }
                    if (values.AsSlice()[0] != 12) {
                        return false;
                    }
                    stack mut u32[0 2 ** 31 - 1] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0) {
                        return false;
                    }

                    stack mut Stack<u32[0 2 ** 31 - 1]> numbers = new();
                    if (!Ok(numbers.Push(20))) {
                        return false;
                    }
                    if (numbers.Peek() != 20) {
                        return false;
                    }
                    if (!numbers.TryPop(popped) || popped != 20 || numbers.Count() != 0) {
                        return false;
                    }

                    stack mut Queue<u32[0 2 ** 31 - 1]> queue = new();
                    if (!Ok(queue.Enqueue(30))) {
                        return false;
                    }
                    if (queue.Peek() != 30) {
                        return false;
                    }
                    if (!queue.TryDequeue(popped) || popped != 30 || queue.Count() != 0) {
                        return false;
                    }

                    stack mut LinkedList<u32[0 2 ** 31 - 1]> linked = new();
                    if (!Ok(linked.ReserveNodes(2)) || linked.Count() != 0) {
                        return false;
                    }
                    if (!Ok(linked.AddFirst(40))) {
                        return false;
                    }
                    if (!Ok(linked.AddLast(50))) {
                        return false;
                    }
                    if (!linked.TryRemoveFirst(popped) || popped != 40 || linked.Count() != 1) {
                        return false;
                    }
                    if (!linked.TryRemoveLast(popped) || popped != 50 || linked.Count() != 0) {
                        return false;
                    }

                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack u32[0 2 ** 31 - 1] dictionaryKey = 3;
                    if (!Ok(dictionary.Set(dictionaryKey, 33))) {
                        return false;
                    }
                    if (!dictionary.ContainsKey(dictionaryKey)) {
                        return false;
                    }
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    if (!dictionary.TryGet(dictionaryKey, found) || found != 33) {
                        return false;
                    }
                    if (!dictionary.Remove(dictionaryKey) || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0) {
                        return false;
                    }

                    stack mut List<u32[0 2 ** 31 - 1]> customList = new();
                    if (!Ok(customList.Push(1)) || !Ok(customList.Push(2)) || customList.Count() != 2) {
                        return false;
                    }

                    stack mut Queue<u32[0 2 ** 31 - 1]> customQueue = new();
                    if (!Ok(customQueue.Enqueue(3)) || !Ok(customQueue.Enqueue(4)) || customQueue.Count() != 2) {
                        return false;
                    }

                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> customDictionary = new();
                    stack u32[0 2 ** 31 - 1] customDictionaryKey = 9;
                    if (!Ok(customDictionary.Set(customDictionaryKey, 18)) || !customDictionary.ContainsKey(customDictionaryKey)) {
                        return false;
                    }

                    return values.Capacity() >= 1;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourcePromotedCollectionsExposeDynamicComparisonTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedCollectionsSurface.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UsePromotedCollections() {
                    stack mut System.Collections.List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Push(10))) {
                        return false;
                    }

                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12 || values.AsSlice()[0] != 12) {
                        return false;
                    }

                    stack mut u32[0 2 ** 31 - 1] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stackValues = new();
                    if (!Ok(stackValues.Push(20)) || stackValues.Peek() != 20) {
                        return false;
                    }

                    if (!stackValues.TryPop(popped) || popped != 20 || stackValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> queueValues = new();
                    if (!Ok(queueValues.Enqueue(30)) || queueValues.Peek() != 30) {
                        return false;
                    }

                    if (!queueValues.TryDequeue(popped) || popped != 30 || queueValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ringValues = new();
                    if (!Ok(ringValues.Enqueue(40)) || !Ok(ringValues.Enqueue(41))) {
                        return false;
                    }

                    if (!ringValues.TryDequeue(popped) || popped != 40 || ringValues.Count() != 1) {
                        return false;
                    }

                    stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> linkedValues = new();
                    if (!Ok(linkedValues.ReserveNodes(2)) || !Ok(linkedValues.AddFirst(50)) || !Ok(linkedValues.AddLast(51))) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveFirst(popped) || popped != 50 || linkedValues.Count() != 1) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveLast(popped) || popped != 51 || linkedValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack u32[0 2 ** 31 - 1] dictionaryKey = 3;
                    if (!Ok(dictionary.Reserve(8)) || !Ok(dictionary.Set(dictionaryKey, 33))) {
                        return false;
                    }

                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    if (!dictionary.ContainsKey(dictionaryKey) || !dictionary.TryGet(dictionaryKey, found) || found != 33) {
                        return false;
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 44))) {
                        return false;
                    }

                    if (!dictionary.TryGet(dictionaryKey, found) || found != 44) {
                        return false;
                    }

                    if (!dictionary.TryRemove(dictionaryKey, found) || found != 44 || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0) {
                        return false;
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 55))) {
                        return false;
                    }

                    stack DictionaryRemoveResult<u32[0 2 ** 31 - 1]> removed = dictionary.RemoveMove(dictionaryKey);
                    switch (removed) {
                        case DictionaryRemoveResult<u32[0 2 ** 31 - 1]>.Missing:
                            return false;
                        case DictionaryRemoveResult<u32[0 2 ** 31 - 1]>.Removed(var removedValue):
                            if (removedValue != 55 || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0) {
                                return false;
                            }
                    }

                    if (!Ok(dictionary.Set(dictionaryKey, 66))) {
                        return false;
                    }

                    return dictionary.Remove(dictionaryKey) && !dictionary.ContainsKey(dictionaryKey) && dictionary.Count() == 0;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
    }

    [Fact]
    public void StdLibSourcePromotedListLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedListLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn u64[0 2 ** 63 - 1] GrowAndSlice() {
                    stack mut System.Collections.List<u32[0 2 ** 31 - 1]> values = new();
                    if (!Ok(values.Reserve(8))) {
                        return 0;
                    }

                    for willexit (stack mut u8[0 8] i = 0; i < 8; i += 1) {
                        if (!Ok(values.Push(i))) {
                            return 0;
                        }
                    }

                    values.AsMutableSlice()[3] = 99;
                    return (u64[0 2 ** 63 - 1])values.AsSlice()[3];
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("@__stark_runtime_try_realloc", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("__stark_dynamic_try_reserve", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceDictionaryRawSparseStorageStaysInternalAndJustified()
    {
        var repositoryRoot = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(repositoryRoot, "stdlib", "src", "System", "Collections.stark"));

        Assert.Contains("Raw pointer boundary: Dictionary keeps sparse key/value/control storage", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<K> Keys;", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<V> Values;", source, StringComparison.Ordinal);
        Assert.Contains("internal rawmutptr<u8[0 2]> States;", source, StringComparison.Ordinal);
        Assert.Contains("internal System.Memory.Allocation KeysAllocation;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public rawptr", source, StringComparison.Ordinal);
        Assert.DoesNotContain("public rawmutptr", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedDictionaryUsesSparseRawValueStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedDictionaryLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowDictionary() {
                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1) {
                        stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i + 7);
                        if (!Ok(dictionary.Set(i, value))) {
                            return false;
                        }
                    }

                    stack u32[0 2 ** 31 - 1] lookupKey = 17;
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    return dictionary.Capacity() >= 32
                        && dictionary.TryGet(lookupKey, found)
                        && found == 24
                        && dictionary.Remove(lookupKey)
                        && dictionary.Count() == 31;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", llvm.Text, StringComparison.Ordinal);

        var reserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_Reserve__u32_0_2147483647__u32_0_2147483647(",
            "Expected Dictionary.Reserve specialization to be emitted.");
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__u32_0_2147483647__u32_0_2147483647(",
            "Expected Dictionary.TryGet specialization to be emitted.");

        Assert.Contains("@System_Memory_Allocate(", reserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("switch", tryGetBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DictionaryValueSlot", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotedDictionaryLookupUsesGroupedControlByteProbe()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "DictionaryLookup.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var findIndexBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindIndex__u32__u32(");
        var findInsertionBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindInsertionIndex__u32__u32(");
        var initializeBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc void @__stark_mono_fn_System_Collections__System_Collections_Dictionary_InitializeStates__u32__u32(");

        Assert.DoesNotContain("; LLVM body emission fallback", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("br i1 undef", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryStateWordAt", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("FindDictionaryEmptyStateIndex", llvm, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeDictionaryStates", llvm, StringComparison.Ordinal);
        Assert.Contains("load i64", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("72340172838076673", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("-9187201950435737472", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("TrailingZeroCount", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("load i64", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("72340172838076673", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("-9187201950435737472", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("TrailingZeroCount", findInsertionBody, StringComparison.Ordinal);
        Assert.Contains("llvm.memset.p0.i64", initializeBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourcePromotedCollectionReservesUseSparseSlotStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibPromotedCollectionReserveLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowCollections() {
                    stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> queue = new();
                    stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    return Ok(queue.Reserve(32)) && Ok(dictionary.Reserve(32));
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm",
                OptimizationLevel: CompilerOptimizationLevel.O0));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);

        var ringQueueReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_RingQueue_Reserve__u32_0_2147483647(");
        var sparseReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_SparseSlots_ReserveRing__u32_0_2147483647(");
        var dictionaryReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_Reserve__u32_0_2147483647__u32_0_2147483647(");

        Assert.Contains("SparseSlots_ReserveRing__u32", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Allocate(", sparseReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", sparseReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("QueueSlot", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_addedSlots", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memmove", sparseReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Allocate(", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_nextValueSlots", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", dictionaryReserveBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceDictionaryGrowthLowersThroughSharedCapacityHelper()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibDictionaryGrowth.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status) {
                    switch (status) {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowDictionary() {
                    stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
                    stack mut u32[0 2 ** 31 - 1] index = 0;
                    while willexit (index < 9) {
                        stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(index + 1);
                        if (!Ok(dictionary.Set(index, value))) {
                            return false;
                        }

                        index += 1;
                    }

                    stack u32[0 2 ** 31 - 1] lookupKey = 4;
                    stack mut u32[0 2 ** 31 - 1] found = 0;
                    return dictionary.Capacity() >= 16
                        && dictionary.TryGet(lookupKey, found)
                        && found == 5;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("ComputeHashStorageGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("ComputeContiguousGrowthCapacity", llvm.Text, StringComparison.Ordinal);
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__u32_0_2147483647__u32_0_2147483647",
            "Expected integer Dictionary.TryGet specialization to be emitted.");
        Assert.DoesNotContain(" srem i64 ", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__", tryGetBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibCollectionsGrowMoveDropExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-collections-source-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, CollectionsGrowthMoveDropProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedListMatchesStableListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedListParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedStackMatchesStableStackExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-stack-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedStackParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedQueueMatchesStableQueueExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedQueueParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedRingQueueCandidateExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-ring-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedRingQueueCandidateProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedLinkedListMatchesStableLinkedListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-linked-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedLinkedListParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public void PromotedLinkedListReserveNodesDoesNotEagerlyBuildFreeList()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "LinkedListReservedPush.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var reserveBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_ReserveNodes__u32(");
        var allocateBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_AllocateNode__u32(");

        Assert.Contains("__stark_dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListValueSlot", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListLinks", reserveBody, StringComparison.Ordinal);
        Assert.Contains("LinkedListValueSlot", allocateBody, StringComparison.Ordinal);
        Assert.Contains("LinkedList_ReserveNodes__u32", allocateBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue %System_Collections_LinkedListValueSlot_u32_ zeroinitializer, i8 1", allocateBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PromotedQueueTryDequeueUsesSparseSlotRingPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var benchmarkPath = Path.Combine(repositoryRoot, "benchmarks", "collections", "QueueChurn.stark");
        var targetInfo = new LlvmTargetInfo("x86_64-unknown-linux-gnu", null);
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(File.ReadAllText(benchmarkPath), benchmarkPath),
            new CompilerOptions(
                OptimizationLevel: CompilerOptimizationLevel.O3,
                EmitLlvmIr: true,
                TargetInfo: targetInfo,
                ModuleResolver: new TargetAwareStdLibModuleResolver(
                    new FileSystemModuleResolver(sourceRoot),
                    [sourceRoot],
                    targetInfo)));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        var llvm = result.Artifacts.GetRequired(CompilerArtifactKeys.LlvmIrModule).Text;
        var tryDequeueBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Queue_TryDequeue__u32(");

        var sparseMoveBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef i32 @__stark_mono_fn_System_Collections__System_Collections_SparseSlots_MoveAt__u32(");

        Assert.Contains("SparseSlots_MoveAt__u32", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("store i32", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("getelementptr i32", sparseMoveBody, StringComparison.Ordinal);
        Assert.Contains("load i32", sparseMoveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("System_Collections_QueueSlot_u32_", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_move_at", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memmove", tryDequeueBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibPromotedDictionaryExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-dictionary-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedDictionaryProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task SourceStdLibPromotedCollectionsCrossFamilyParityExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-promoted-collections-cross-family-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedCollectionsCrossFamilyParityProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", sourceRoot, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, tempDirectory.FullName, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    [Fact]
    public async Task PackagedStdLibCollectionsGrowMoveDropExecutableRunsWithoutSource()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var systemPath = Path.Combine(repositoryRoot, "stdlib", "src", "System.stark");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-collections-");
        var packageDirectory = Path.Combine(tempDirectory.FullName, "packages");
        var appDirectory = Path.Combine(tempDirectory.FullName, "app");
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(appDirectory);

        var libraryPath = Path.Combine(packageDirectory, OperatingSystem.IsWindows() ? "System.lib" : "libSystem.a");
        var appPath = Path.Combine(appDirectory, "App.stark");
        var outputPath = Path.Combine(appDirectory, OperatingSystem.IsWindows() ? "app.exe" : "app");

        try
        {
            var buildStdout = new StringWriter();
            var buildStderr = new StringWriter();
            var buildExitCode = await CompilerCli.RunAsync(
                [systemPath, "--emit-lib", "-o", libraryPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                buildStdout,
                buildStderr);

            Assert.True(
                buildExitCode == 0,
                buildStdout + Environment.NewLine + buildStderr);
            AssertCompilerLogsEmitted(buildStderr.ToString());

            await File.WriteAllTextAsync(appPath, CollectionsGrowthMoveDropProgram);

            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var exitCode = await CompilerCli.RunAsync(
                [appPath, "--emit-exe", "-I", packageDirectory, "-o", outputPath, "--target", targetInfo.Triple],
                new StringReader(string.Empty),
                stdout,
                stderr);

            Assert.True(
                exitCode == 0,
                stdout + Environment.NewLine + stderr);
            Assert.Contains("Emitted executable:", stdout.ToString());
            AssertCompilerLogsEmitted(stderr.ToString());
            Assert.True(File.Exists(outputPath));

            var execution = await RunProcessWithUtf8StdinAsync(outputPath, appDirectory, string.Empty);
            Assert.Equal(0, execution.ExitCode);
            Assert.Equal(string.Empty, execution.Stdout);
            Assert.Equal(string.Empty, execution.Stderr);
        }
        finally
        {
            try
            {
                tempDirectory.Delete(recursive: true);
            }
            catch
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string ExtractDefinedFunctionText(string llvm, string signaturePrefix)
    {
        var functionStart = llvm.IndexOf(signaturePrefix, StringComparison.Ordinal);
        Assert.True(functionStart >= 0, $"Expected '{signaturePrefix}' definition to be emitted.");

        var bodyStart = llvm.IndexOf('{', functionStart);
        Assert.True(bodyStart > functionStart, $"Expected '{signaturePrefix}' to include a function body.");

        var depth = 0;
        for (var index = bodyStart; index < llvm.Length; index++)
        {
            var current = llvm[index];
            if (current == '{')
            {
                depth++;
            }
            else if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return llvm.Substring(functionStart, index - functionStart + 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Expected '{signaturePrefix}' body to terminate in emitted LLVM.");
    }
}
