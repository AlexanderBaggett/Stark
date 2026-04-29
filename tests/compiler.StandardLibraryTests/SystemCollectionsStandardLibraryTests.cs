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

        export ffi fn i32[min max] main() {
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
        import System.Experimental.Collections
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

        export ffi fn i32[min max] main() {
            stack mut System.Collections.List<i32[0 max]> stable = new();
            stack mut System.Experimental.Collections.List<i32[0 max]> experimental = new();

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
                stack mut System.Experimental.Collections.List<Resource> experimentalDrops = new();
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
                stack mut System.Experimental.Collections.List<Resource> scopedDrops = new();
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
        import System.Experimental.Collections
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

        export ffi fn i32[min max] main() {
            stack mut System.Collections.Stack<i32[0 max]> stable = new();
            stack mut System.Experimental.Collections.Stack<i32[0 max]> experimental = new();

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
                stack mut System.Experimental.Collections.Stack<Resource> experimentalDrops = new();
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
                stack mut System.Experimental.Collections.Stack<Resource> scopedDrops = new();
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
        import System.Experimental.Collections
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

        export ffi fn i32[min max] main() {
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Experimental.Collections.Queue<i32[0 max]> experimental = new();

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
                stack mut System.Experimental.Collections.Queue<Resource> experimentalDrops = new();
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
                stack mut System.Experimental.Collections.Queue<Resource> scopedDrops = new();
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
        import System.Experimental.Collections
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

        export ffi fn i32[min max] main() {
            stack mut System.Collections.Queue<i32[0 max]> stable = new();
            stack mut System.Experimental.Collections.RingQueue<i32[0 max]> ring = new();

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
                stack mut System.Experimental.Collections.RingQueue<Resource> ringDrops = new();
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
                stack mut System.Experimental.Collections.RingQueue<Resource> scopedDrops = new();
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
        import System.Experimental.Collections
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

        export ffi fn i32[min max] main() {
            stack mut System.Collections.LinkedList<i32[0 max]> stable = new();
            stack mut System.Experimental.Collections.LinkedList<i32[0 max]> experimental = new();

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
                stack mut System.Experimental.Collections.LinkedList<Resource> drops = new();
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
                stack mut System.Experimental.Collections.LinkedList<Resource> scopedDrops = new();
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

                    stack System.Memory.Allocator listAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut List<i32[0 max]> customList = new(listAllocator);
                    if (!Ok(customList.Push(1)) || !Ok(customList.Push(2)) || customList.Count() != 2) {
                        return false;
                    }

                    stack System.Memory.Allocator queueAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut Queue<i32[0 max]> customQueue = new(queueAllocator);
                    if (!Ok(customQueue.Enqueue(3)) || !Ok(customQueue.Enqueue(4)) || customQueue.Count() != 2) {
                        return false;
                    }

                    stack System.Memory.Allocator dictionaryAllocator = new System.Memory.Allocator() {
                        Kind = 7
                    };
                    stack mut Dictionary<i32[0 max], i32[0 max]> customDictionary = new(dictionaryAllocator);
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
                import System.Experimental.Collections
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
                    stack mut System.Experimental.Collections.List<i32[0 max]> values = new();
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

                    stack mut System.Experimental.Collections.Stack<i32[0 max]> stackValues = new();
                    if (!Ok(stackValues.Push(20)) || stackValues.Peek() != 20) {
                        return false;
                    }

                    if (!stackValues.TryPop(popped) || popped != 20 || stackValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Experimental.Collections.Queue<i32[0 max]> queueValues = new();
                    if (!Ok(queueValues.Enqueue(30)) || queueValues.Peek() != 30) {
                        return false;
                    }

                    if (!queueValues.TryDequeue(popped) || popped != 30 || queueValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Experimental.Collections.RingQueue<i32[0 max]> ringValues = new();
                    if (!Ok(ringValues.Enqueue(40)) || !Ok(ringValues.Enqueue(41))) {
                        return false;
                    }

                    if (!ringValues.TryDequeue(popped) || popped != 40 || ringValues.Count() != 1) {
                        return false;
                    }

                    stack mut System.Experimental.Collections.LinkedList<i32[0 max]> linkedValues = new();
                    if (!Ok(linkedValues.ReserveNodes(2)) || !Ok(linkedValues.AddFirst(50)) || !Ok(linkedValues.AddLast(51))) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveFirst(popped) || popped != 50 || linkedValues.Count() != 1) {
                        return false;
                    }

                    if (!linkedValues.TryRemoveLast(popped) || popped != 51 || linkedValues.Count() != 0) {
                        return false;
                    }

                    stack mut System.Experimental.Collections.Dictionary<i32[0 max], i32[0 max]> dictionary = new();
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
                import System.Experimental.Collections
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
                    stack mut System.Experimental.Collections.List<i32[0 max]> values = new();
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
        Assert.Contains("dynamic_try_reserve_needed", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("extractvalue { ptr, i64, i64 }", llvm.Text, StringComparison.Ordinal);
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
        Assert.Contains(" = and i64 ", tryGetBody, StringComparison.Ordinal);
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
}
