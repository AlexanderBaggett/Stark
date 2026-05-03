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

        fn bool IsPowerOfTwo(i64[0 max] value) {
            if (value == 0) {
                return false;
            }

            stack i64[0 max] mask = (i64[0 max])(value - 1);
            return (value & mask) == 0;
        }

        fn bool ConsumeList(List<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Capacity() >= expected;
        }

        fn bool ConsumeStack(Stack<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Peek() == 79;
        }

        fn bool ConsumeQueue(Queue<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected && values.Peek() == 0;
        }

        fn bool ConsumeLinkedList(LinkedList<i32[0 max]> values, i64[0 max] expected) {
            return values.Count() == expected;
        }

        fn bool ConsumeDictionary(Dictionary<i32[0 max], i32[0 max]> values, i64[0 max] expected) {
            stack i32[0 max] key = 17;
            stack mut i32[0 max] found = 0;
            return values.Count() == expected
                && IsPowerOfTwo(values.Capacity())
                && values.ContainsKey(key)
                && values.TryGet(key, found)
                && found == 34;
        }

        export unsafe ffi fn i32[min max] main() {
            stack mut List<i32[0 max]> list = new();
            for willexit (stack mut i32[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(list.Push(i))) {
                    return 1;
                }
            }

            if (!ConsumeList(list, 96)) {
                return 2;
            }

            stack mut Stack<i32[0 max]> stackValues = new();
            for willexit (stack mut i32[0 80] i = 0; i < 80; i += 1) {
                if (!Ok(stackValues.Push(i))) {
                    return 3;
                }
            }

            if (!ConsumeStack(stackValues, 80)) {
                return 4;
            }

            stack mut Queue<i32[0 max]> queue = new();
            for willexit (stack mut i32[0 96] i = 0; i < 96; i += 1) {
                if (!Ok(queue.Enqueue(i))) {
                    return 5;
                }
            }

            if (!ConsumeQueue(queue, 96)) {
                return 6;
            }

            stack mut LinkedList<i32[0 max]> linked = new();
            for willexit (stack mut i32[0 48] i = 0; i < 48; i += 1) {
                if (!Ok(linked.AddLast(i))) {
                    return 7;
                }
            }

            if (!ConsumeLinkedList(linked, 48)) {
                return 8;
            }

            stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity())) {
                return 9;
            }

            for willexit (stack mut i32[0 64] i = 0; i < 64; i += 1) {
                stack i32[0 max] key = i;
                stack i32[0 max] value = (i32[0 max])(i * 2);
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

    private const string ExperimentalListParityProgram = """
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
            stack mut System.Collections.List<i32[0 max]> stable = new();
            stack mut System.Collections.List<i32[0 max]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0))) {
                return 1;
            }

            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
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

            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i]) {
                    return 4;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (experimental.Count() > 0) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
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

            if (!TooLarge(stable.Reserve(9223372036854775807)) || !TooLarge(experimental.Reserve(9223372036854775807))) {
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

    private const string ExperimentalStackParityProgram = """
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
            stack mut System.Collections.Stack<i32[0 max]> stable = new();
            stack mut System.Collections.Stack<i32[0 max]> experimental = new();

            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
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
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
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

    private const string ExperimentalQueueParityProgram = """
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
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Collections.Queue<i32[0 max]> experimental = new();

            if (!Ok(stable.Reserve(0)) || !Ok(experimental.Reserve(0))) {
                return 1;
            }

            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
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
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
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

            if (!TooLarge(stable.Reserve(9223372036854775807)) || !TooLarge(experimental.Reserve(9223372036854775807))) {
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

    private const string ExperimentalRingQueueCandidateProgram = """
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
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Collections.RingQueue<i32[0 max]> ring = new();

            for willexit (stack mut i32[0 64] i = 0; i < 64; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return 1;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i32[0 32] i = 0; i < 32; i += 1) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue)) {
                    return 2;
                }

                if (stableValue != ringValue) {
                    return 3;
                }

                checksum += (i64[min max])stableValue;
            }

            for willexit (stack mut i32[0 128] i = 64; i < 128; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return 4;
                }
            }

            if (stable.Count() != ring.Count() || ring.Capacity() < ring.Count()) {
                return 5;
            }

            while willexit (!ring.IsEmpty()) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] ringValue = 0;
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

    private const string ExperimentalLinkedListParityProgram = """
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
            stack mut System.Collections.LinkedList<i32[0 max]> stable = new();
            stack mut System.Collections.LinkedList<i32[0 max]> experimental = new();

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

            stack mut i32[0 max] stableValue = 0;
            stack mut i32[0 max] experimentalValue = 0;
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
            for willexit (stack mut i32[0 64] i = 0; i < 64; i += 1) {
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

    private const string ExperimentalDictionaryProgram = """
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

        fn bool IsPowerOfTwo(i64[0 max] value) {
            if (value == 0) {
                return false;
            }

            stack i64[0 max] mask = (i64[0 max])(value - 1);
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
            stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity())) {
                return 1;
            }

            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
                stack i32[0 max] key = i;
                stack i32[0 max] value = (i32[0 max])(i * 5);
                if (!Ok(dictionary.Set(key, value)) || !IsPowerOfTwo(dictionary.Capacity())) {
                    return 2;
                }
            }

            if (dictionary.Count() != 128 || dictionary.IsEmpty()) {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            stack mut i32[0 max] found = 0;
            stack i32[0 max] refKey = 7;
            stack i64[0 max] refIndex = dictionary.FindIndex(refKey);
            if (!dictionary.ContainsIndex(refIndex) || dictionary.GetAtIndex(refIndex) != 35) {
                return 25;
            }

            dictionary.GetMutAtIndex(refIndex) = 36;
            if (dictionary.GetAtIndex(refIndex) != 36) {
                return 26;
            }

            dictionary.GetMutAtIndex(refIndex) = 35;
            for willexit (stack mut i32[0 128] i = 0; i < 128; i += 1) {
                stack i32[0 max] key = i;
                if (!dictionary.ContainsKey(key) || !dictionary.TryGet(key, found) || found != (i32[0 max])(i * 5)) {
                    return 4;
                }

                checksum += (i64[min max])found;
            }

            if (checksum != 40640) {
                return 5;
            }

            stack i32[0 max] updateKey = 64;
            if (!Ok(dictionary.Set(updateKey, 999)) || !dictionary.TryGet(updateKey, found) || found != 999 || dictionary.Count() != 128) {
                return 6;
            }

            for willexit (stack mut i32[0 64] i = 0; i < 64; i += 1) {
                stack i32[0 max] key = (i32[0 max])(i * 2);
                if (!dictionary.Remove(key)) {
                    return 7;
                }
            }

            if (dictionary.Count() != 64) {
                return 8;
            }

            stack i32[0 max] removedKey = 65;
            if (!dictionary.TryRemove(removedKey, found) || found != 325 || dictionary.ContainsKey(removedKey) || dictionary.Count() != 63) {
                return 9;
            }

            stack i32[0 max] tombstoneKey = 4096;
            if (!Ok(dictionary.Set(tombstoneKey, 12345)) || !dictionary.TryGet(tombstoneKey, found) || found != 12345) {
                return 10;
            }

            dictionary.Clear();
            if (!dictionary.IsEmpty() || dictionary.Count() != 0 || dictionary.ContainsKey(tombstoneKey)) {
                return 11;
            }

            {
                stack mut System.Collections.Dictionary<i32[0 max], Resource> drops = new();
                stack i32[0 max] keyOne = 1;
                stack i32[0 max] keyTwo = 2;
                stack i32[0 max] keyThree = 3;
                stack i32[0 max] keyFour = 4;
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
                stack mut System.Collections.Dictionary<i32[0 max], Resource> scopedDrops = new();
                stack i32[0 max] scopedKey = 7;
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

    private const string ExperimentalCollectionsCrossFamilyParityProgram = """
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
            stack mut System.Collections.List<i32[0 max]> stable = new();
            stack mut System.Collections.List<i32[0 max]> experimental = new();
            if (!Ok(stable.Reserve(3)) || !Ok(experimental.Reserve(3))) {
                return false;
            }

            for willexit (stack mut i32[0 40] i = 0; i < 40; i += 1) {
                stack i32[0 max] value = (i32[0 max])((i * 3) + 1);
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

            for willexit (stack mut i32[0 40] i = 0; i < 40; i += 1) {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i]) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i32[0 20] i = 0; i < 20; i += 1) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
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
            stack i64[0 max] impossible = (i64[0 max])((2**63) - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckStackParity() {
            stack mut System.Collections.Stack<i32[0 max]> stable = new();
            stack mut System.Collections.Stack<i32[0 max]> experimental = new();
            if (!Ok(stable.Reserve(2)) || !Ok(experimental.Reserve(2))) {
                return false;
            }

            for willexit (stack mut i32[0 32] i = 0; i < 32; i += 1) {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i)) || stable.Peek() != experimental.Peek()) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty()) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            stack i64[0 max] impossible = (i64[0 max])((2**63) - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && checksum == 496
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckQueueParity() {
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Collections.Queue<i32[0 max]> experimental = new();
            if (!Ok(stable.Reserve(4)) || !Ok(experimental.Reserve(4))) {
                return false;
            }

            for willexit (stack mut i32[0 48] i = 0; i < 48; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i)) || stable.Peek() != experimental.Peek()) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i32[0 16] i = 0; i < 16; i += 1) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            for willexit (stack mut i32[0 72] i = 48; i < 72; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i))) {
                    return false;
                }
            }

            while willexit (!experimental.IsEmpty()) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue) {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            stack i64[0 max] impossible = (i64[0 max])((2**63) - 1);
            return stable.IsEmpty()
                && experimental.IsEmpty()
                && checksum == 2556
                && TooLarge(stable.Reserve(impossible))
                && TooLarge(experimental.Reserve(impossible));
        }

        fn bool CheckRingQueueParity() {
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Collections.RingQueue<i32[0 max]> ring = new();
            for willexit (stack mut i32[0 32] i = 0; i < 32; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut i32[0 12] i = 0; i < 12; i += 1) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue) {
                    return false;
                }

                checksum += (i64[min max])ringValue;
            }

            for willexit (stack mut i32[0 64] i = 32; i < 64; i += 1) {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i))) {
                    return false;
                }
            }

            while willexit (!ring.IsEmpty()) {
                stack mut i32[0 max] stableValue = 0;
                stack mut i32[0 max] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue) {
                    return false;
                }

                checksum += (i64[min max])ringValue;
            }

            stack i64[0 max] impossible = (i64[0 max])((2**63) - 1);
            return stable.IsEmpty()
                && ring.IsEmpty()
                && checksum == 2016
                && TooLarge(ring.Reserve(impossible));
        }

        fn bool CheckLinkedListParity() {
            stack mut System.Collections.LinkedList<i32[0 max]> stable = new();
            stack mut System.Collections.LinkedList<i32[0 max]> experimental = new();
            if (!Ok(stable.ReserveNodes(3)) || !Ok(experimental.ReserveNodes(3))) {
                return false;
            }

            if (!Ok(stable.AddLast(10)) || !Ok(experimental.AddLast(10))
                || !Ok(stable.AddLast(20)) || !Ok(experimental.AddLast(20))
                || !Ok(stable.AddFirst(5)) || !Ok(experimental.AddFirst(5))
                || !Ok(stable.AddLast(30)) || !Ok(experimental.AddLast(30))) {
                return false;
            }

            stack mut i32[0 max] stableValue = 0;
            stack mut i32[0 max] experimentalValue = 0;
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

            for willexit (stack mut i32[0 24] i = 0; i < 24; i += 1) {
                if (!Ok(stable.AddLast(i)) || !Ok(experimental.AddLast(i))) {
                    return false;
                }
            }

            stable.Clear();
            experimental.Clear();
            return stable.Count() == 0 && experimental.Count() == 0;
        }

        fn bool CheckDictionaryParity() {
            stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> stable = new();
            stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> experimental = new();
            if (!Ok(stable.Reserve(5)) || !Ok(experimental.Reserve(5))) {
                return false;
            }

            for willexit (stack mut i32[0 48] i = 0; i < 48; i += 1) {
                stack i32[0 max] key = (i32[0 max])(i * 2);
                stack i32[0 max] value = (i32[0 max])(i + 100);
                if (!Ok(stable.Set(key, value)) || !Ok(experimental.Set(key, value))) {
                    return false;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count()) {
                return false;
            }

            stack mut i64[min max] checksum = 0;
            stack mut i32[0 max] stableFound = 0;
            stack mut i32[0 max] experimentalFound = 0;
            for willexit (stack mut i32[0 48] i = 0; i < 48; i += 1) {
                stack i32[0 max] key = (i32[0 max])(i * 2);
                if (!stable.TryGet(key, stableFound) || !experimental.TryGet(key, experimentalFound) || stableFound != experimentalFound) {
                    return false;
                }

                checksum += (i64[min max])experimentalFound;
            }

            if (checksum != 5928) {
                return false;
            }

            stack i32[0 max] updateKey = 20;
            if (!Ok(stable.Set(updateKey, 999)) || !Ok(experimental.Set(updateKey, 999))
                || stable.Count() != experimental.Count()
                || !stable.TryGet(updateKey, stableFound)
                || !experimental.TryGet(updateKey, experimentalFound)
                || stableFound != experimentalFound
                || experimentalFound != 999) {
                return false;
            }

            for willexit (stack mut i32[0 12] i = 0; i < 12; i += 1) {
                stack i32[0 max] key = (i32[0 max])(i * 4);
                if (!stable.Remove(key) || !experimental.Remove(key) || stable.ContainsKey(key) || experimental.ContainsKey(key)) {
                    return false;
                }
            }

            stack i32[0 max] tombstoneKey = 777;
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
            stack i64[0 max] impossible = (i64[0 max])((2**63) - 1);
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
                stack mut System.Collections.Dictionary<i32[0 max], Resource> values = new();
                stack i32[0 max] one = 1;
                stack i32[0 max] two = 2;
                if (!Ok(values.Set(one, new Resource() { Value = 17 })) || !Ok(values.Set(two, new Resource() { Value = 18 }))) {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 171) {
                return false;
            }

            {
                stack mut System.Collections.Dictionary<i32[0 max], Resource> values = new();
                stack i32[0 max] three = 3;
                stack i32[0 max] four = 4;
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
                    stack mut List<i32[0 max]> values = new();
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
                    stack mut i32[0 max] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0) {
                        return false;
                    }

                    stack mut Stack<i32[0 max]> numbers = new();
                    if (!Ok(numbers.Push(20))) {
                        return false;
                    }
                    if (numbers.Peek() != 20) {
                        return false;
                    }
                    if (!numbers.TryPop(popped) || popped != 20 || numbers.Count() != 0) {
                        return false;
                    }

                    stack mut Queue<i32[0 max]> queue = new();
                    if (!Ok(queue.Enqueue(30))) {
                        return false;
                    }
                    if (queue.Peek() != 30) {
                        return false;
                    }
                    if (!queue.TryDequeue(popped) || popped != 30 || queue.Count() != 0) {
                        return false;
                    }

                    stack mut LinkedList<i32[0 max]> linked = new();
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

                    stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    stack i32[0 max] dictionaryKey = 3;
                    if (!Ok(dictionary.Set(dictionaryKey, 33))) {
                        return false;
                    }
                    if (!dictionary.ContainsKey(dictionaryKey)) {
                        return false;
                    }
                    stack mut i32[0 max] found = 0;
                    if (!dictionary.TryGet(dictionaryKey, found) || found != 33) {
                        return false;
                    }
                    if (!dictionary.Remove(dictionaryKey) || dictionary.ContainsKey(dictionaryKey) || dictionary.Count() != 0) {
                        return false;
                    }

                    stack mut List<i32[0 max]> customList = new();
                    if (!Ok(customList.Push(1)) || !Ok(customList.Push(2)) || customList.Count() != 2) {
                        return false;
                    }

                    stack mut Queue<i32[0 max]> customQueue = new();
                    if (!Ok(customQueue.Enqueue(3)) || !Ok(customQueue.Enqueue(4)) || customQueue.Count() != 2) {
                        return false;
                    }

                    stack mut Dictionary<i32[0 max], i32[0 max]> customDictionary = new();
                    stack i32[0 max] customDictionaryKey = 9;
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
    public void StdLibSourceExperimentalCollectionsExposeDynamicComparisonTypes()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalCollectionsSurface.stark");
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

                fn bool UseExperimentalCollections() {
                    stack mut System.Collections.List<i32[0 max]> values = new();
                    if (!Ok(values.Push(10))) {
                        return false;
                    }

                    values.GetMut(0) = 11;
                    values.AsMutableSlice()[0] = 12;
                    if (values.Get(0) != 12 || values.AsSlice()[0] != 12) {
                        return false;
                    }

                    stack mut i32[0 max] popped = 0;
                    if (!values.TryPop(popped) || popped != 12 || values.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Stack<i32[0 max]> stackValues = new();
                    if (!Ok(stackValues.Push(20)) || stackValues.Peek() != 20) {
                        return false;
                    }

                    if (!stackValues.TryPop(popped) || popped != 20 || stackValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Queue<i32[0 max]> queueValues = new();
                    if (!Ok(queueValues.Enqueue(30)) || queueValues.Peek() != 30) {
                        return false;
                    }

                    if (!queueValues.TryDequeue(popped) || popped != 30 || queueValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.RingQueue<i32[0 max]> ringValues = new();
                    if (!Ok(ringValues.Enqueue(40)) || !Ok(ringValues.Enqueue(41))) {
                        return false;
                    }

                    if (!ringValues.TryDequeue(popped) || popped != 40 || ringValues.Count() != 1) {
                        return false;
                    }

                    stack mut System.Collections.LinkedList<i32[0 max]> linkedValues = new();
                    if (!Ok(linkedValues.ReserveNodes(2)) || !Ok(linkedValues.AddFirst(50)) || !Ok(linkedValues.AddLast(51))) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveFirst(popped) || popped != 50 || linkedValues.Count() != 1) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveLast(popped) || popped != 51 || linkedValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    stack i32[0 max] dictionaryKey = 3;
                    if (!Ok(dictionary.Reserve(8)) || !Ok(dictionary.Set(dictionaryKey, 33))) {
                        return false;
                    }

                    stack mut i32[0 max] found = 0;
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

                    stack DictionaryRemoveResult<i32[0 max]> removed = dictionary.RemoveMove(dictionaryKey);
                    switch (removed) {
                        case DictionaryRemoveResult<i32[0 max]>.Missing:
                            return false;
                        case DictionaryRemoveResult<i32[0 max]>.Removed(var removedValue):
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
    public void StdLibSourceExperimentalListLowersThroughDynamicStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalListLowering.stark");
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

                fn i64[0 max] GrowAndSlice() {
                    stack mut System.Collections.List<i32[0 max]> values = new();
                    if (!Ok(values.Reserve(8))) {
                        return 0;
                    }

                    for willexit (stack mut i32[0 8] i = 0; i < 8; i += 1) {
                        if (!Ok(values.Push(i))) {
                            return 0;
                        }
                    }

                    values.AsMutableSlice()[3] = 99;
                    return (i64[0 max])values.AsSlice()[3];
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
    public void StdLibSourceExperimentalDictionaryUsesSparseRawValueStorage()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalDictionaryLowering.stark");
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
                    stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    for willexit (stack mut i32[0 32] i = 0; i < 32; i += 1) {
                        stack i32[0 max] value = (i32[0 max])(i + 7);
                        if (!Ok(dictionary.Set(i, value))) {
                            return false;
                        }
                    }

                    stack i32[0 max] lookupKey = 17;
                    stack mut i32[0 max] found = 0;
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
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_Reserve__i32_0_2147483647__i32_0_2147483647(",
            "Expected Dictionary.Reserve specialization to be emitted.");
        var tryGetBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__i32_0_2147483647__i32_0_2147483647(",
            "Expected Dictionary.TryGet specialization to be emitted.");

        Assert.Contains("@System_Memory_Allocate(", reserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("switch", tryGetBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DictionaryValueSlot", tryGetBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExperimentalDictionaryLookupUsesGroupedControlByteProbe()
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
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindIndex__i32_0_2147483647__i32_0_2147483647(");
        var findInsertionBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_FindInsertionIndex__i32_0_2147483647__i32_0_2147483647(");
        var initializeBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc void @__stark_mono_fn_System_Collections__System_Collections_Dictionary_InitializeStates__i32_0_2147483647__i32_0_2147483647(");

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
    public void StdLibSourceExperimentalCollectionReservesUseTailInitializationRegions()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibExperimentalCollectionReserveLowering.stark");
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
                    stack mut System.Collections.RingQueue<i32[0 max]> queue = new();
                    stack mut System.Collections.Dictionary<i32[0 max], i32[0 max]> dictionary = new();
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
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_RingQueue_Reserve__i32_0_2147483647(",
            "Expected RingQueue.Reserve specialization to be emitted.");
        var dictionaryReserveBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_Dictionary_Reserve__i32_0_2147483647__i32_0_2147483647(",
            "Expected Dictionary.Reserve specialization to be emitted.");

        Assert.Contains("%slot_addedSlots", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Allocate(", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.Contains("@System_Memory_Free(", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryValueSlot", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("%slot_nextValueSlots", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("dynamic_try_reserve", dictionaryReserveBody, StringComparison.Ordinal);
        Assert.Contains("!llvm.access.group", ringQueueReserveBody, StringComparison.Ordinal);
        Assert.Contains("!\"llvm.loop.parallel_accesses\"", llvm.Text, StringComparison.Ordinal);
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
                    stack mut Dictionary<i32[0 max], i32[0 max]> dictionary = new();
                    stack mut i32[0 max] index = 0;
                    while willexit (index < 9) {
                        stack i32[0 max] value = (i32[0 max])(index + 1);
                        if (!Ok(dictionary.Set(index, value))) {
                            return false;
                        }

                        index += 1;
                    }

                    stack i32[0 max] lookupKey = 4;
                    stack mut i32[0 max] found = 0;
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
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Dictionary_TryGet__i32_0_2147483647__i32_0_2147483647",
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
    public async Task SourceStdLibExperimentalListMatchesStableListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalListParityProgram);

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
    public async Task SourceStdLibExperimentalStackMatchesStableStackExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-stack-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalStackParityProgram);

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
    public async Task SourceStdLibExperimentalQueueMatchesStableQueueExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalQueueParityProgram);

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
    public async Task SourceStdLibExperimentalRingQueueCandidateExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-ring-queue-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalRingQueueCandidateProgram);

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
    public async Task SourceStdLibExperimentalLinkedListMatchesStableLinkedListExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-linked-list-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalLinkedListParityProgram);

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
    public void ExperimentalLinkedListReserveNodesDoesNotEagerlyBuildFreeList()
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
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_ReserveNodes__i32_0_2147483647(");
        var allocateBody = ExtractDefinedFunctionText(
            llvm,
            "define linkonce_odr dso_local fastcc noundef %System_Memory_MemoryStatus @__stark_mono_fn_System_Collections__System_Collections_LinkedList_AllocateNode__i32_0_2147483647(");

        Assert.Contains("__stark_dynamic_try_reserve", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListValueSlot", reserveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("LinkedListLinks", reserveBody, StringComparison.Ordinal);
        Assert.Contains("LinkedListValueSlot", allocateBody, StringComparison.Ordinal);
        Assert.Contains("LinkedList_ReserveNodes__i32_0_2147483647", allocateBody, StringComparison.Ordinal);
        Assert.Contains("insertvalue %System_Collections_LinkedListValueSlot_i32_0_2147483647__ zeroinitializer, i8 1", allocateBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ExperimentalQueueTryDequeueUsesHeadLengthRingPath()
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
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_Queue_TryDequeue__i32_0_2147483647(");

        Assert.Contains("getelementptr i32", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("i32 0, i32 2", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("i32 0, i32 3", tryDequeueBody, StringComparison.Ordinal);
        Assert.Contains("i32 0, i32 4", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("__stark_dynamic_move_at", tryDequeueBody, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memmove", tryDequeueBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibExperimentalDictionaryExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-dictionary-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalDictionaryProgram);

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
    public async Task SourceStdLibExperimentalCollectionsCrossFamilyParityExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-experimental-collections-cross-family-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, ExperimentalCollectionsCrossFamilyParityProgram);

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

