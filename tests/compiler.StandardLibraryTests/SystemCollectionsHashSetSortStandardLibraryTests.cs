using Stark.Compiler;

namespace compiler.StandardLibraryTests;

public sealed class SystemCollectionsHashSetSortStandardLibraryTests : StandardLibraryTestSuite
{
    private const string PromotedHashSetProgram = """
        import System.Collections
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool IsPowerOfTwo(u64[0 2 ** 63 - 1] value)
        {
            if (value == 0)
            {
                return false;
            }

            stack u64[0 2 ** 63 - 1] mask = (u64[0 2 ** 63 - 1])(value - 1);
            return (value & mask) == 0;
        }

        export unsafe fn i32[min max] main()
        {
            stack mut System.Collections.HashSet<u32[0 2 ** 31 - 1]> set = new();
            if (!Ok(set.Reserve(3)) || !IsPowerOfTwo(set.Capacity()))
            {
                return 1;
            }

            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                if (!Ok(set.Add(key)) || !IsPowerOfTwo(set.Capacity()))
                {
                    return 2;
                }
            }

            if (set.Count() != 128 || set.IsEmpty())
            {
                return 3;
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 128] i = 0; i < 128; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                if (!set.Contains(key))
                {
                    return 4;
                }

                checksum += (i64[min max])key;
            }

            if (checksum != 8128)
            {
                return 5;
            }

            stack u32[0 2 ** 31 - 1] duplicateKey = 64;
            if (!Ok(set.Add(duplicateKey)) || set.Count() != 128 || !set.Contains(duplicateKey))
            {
                return 6;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                if (!set.Remove(key) || set.Contains(key))
                {
                    return 7;
                }
            }

            if (set.Count() != 64)
            {
                return 8;
            }

            stack u32[0 2 ** 31 - 1] missingKey = 4096;
            if (set.Remove(missingKey) || set.Contains(missingKey))
            {
                return 9;
            }

            {
                stack mut System.Collections.HashSet<u32[0 2 ** 31 - 1]> clustered = new();
                stack u32[0 2 ** 31 - 1] clusterKeyOne = 1;
                stack u32[0 2 ** 31 - 1] clusterKeyTwo = 9;
                stack u32[0 2 ** 31 - 1] clusterKeyThree = 17;
                stack u32[0 2 ** 31 - 1] clusterKeyFour = 25;
                if (!Ok(clustered.Reserve(4))
                    || !Ok(clustered.Add(clusterKeyOne))
                    || !Ok(clustered.Add(clusterKeyTwo))
                    || !Ok(clustered.Add(clusterKeyThree)))
                    {
                        return 10;
                }

                if (!clustered.Remove(clusterKeyOne)
                    || clustered.Contains(clusterKeyOne)
                    || !clustered.Contains(clusterKeyTwo)
                    || !clustered.Contains(clusterKeyThree)
                    || clustered.Count() != 2)
                    {
                        return 11;
                }

                if (!Ok(clustered.Add(clusterKeyFour))
                    || !clustered.Contains(clusterKeyFour)
                    || clustered.Count() != 3)
                    {
                        return 12;
                }
            }

            set.Clear();
            if (!set.IsEmpty() || set.Count() != 0 || set.Contains(duplicateKey))
            {
                return 13;
            }

            return 0;
        }
        """;

    private const string DeterministicSortingProgram = """
        import System.Collections
        import System.Memory
        module App

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        finite law Ordering CompareU32(borrow u32[0 max] left, borrow u32[0 max] right)
            where overlap(left, right)
        {
            if (left < right)
            {
                return Ordering.Less;
            }

            if (left > right)
            {
                return Ordering.Greater;
            }

            return Ordering.Equal;
        }

        struct Item : Ord
        {
            u32[0 max] Key;
            u32[0 max] Value;

            finite law Ordering Compare(borrow Item left, borrow Item right)
                where overlap(left, right)
            {
                return CompareU32(left.Key, right.Key);
            }
        }

        finite law Ordering CompareItemByKey(borrow Item left, borrow Item right)
            where overlap(left, right)
        {
            return CompareU32(left.Key, right.Key);
        }

        fn bool CheckFixedArraySort()
        {
            stack mut u32[0 max][8] values = { 9, 1, 5, 3, 5, 2, 8, 0 };
            SortBy<u32[0 max]>(values, CompareU32);
            return values[0] == 0
                && values[1] == 1
                && values[2] == 2
                && values[3] == 3
                && values[4] == 5
                && values[5] == 5
                && values[6] == 8
                && values[7] == 9;
        }

        fn bool CheckListSliceSort()
        {
            stack mut List<u32[0 max]> values = new();
            if (!Ok(values.Push(42))
                || !Ok(values.Push(7))
                || !Ok(values.Push(19))
                || !Ok(values.Push(7))
                || !Ok(values.Push(31)))
            {
                return false;
            }

            SortBy<u32[0 max]>(values.AsMutableSlice(), CompareU32);
            return values.AsSlice()[0] == 7
                && values.AsSlice()[1] == 7
                && values.AsSlice()[2] == 19
                && values.AsSlice()[3] == 31
                && values.AsSlice()[4] == 42;
        }

        fn bool CheckAggregateSort()
        {
            stack mut Item[5] items =
            {
                new Item() { Key = 30, Value = 1 },
                new Item() { Key = 10, Value = 2 },
                new Item() { Key = 40, Value = 3 },
                new Item() { Key = 20, Value = 4 },
                new Item() { Key = 20, Value = 5 }
            };

            SortBy<Item>(items, CompareItemByKey);
            return items[0].Key == 10
                && items[1].Key == 20
                && items[2].Key == 20
                && items[3].Key == 30
                && items[4].Key == 40;
        }

        fn bool CheckAggregateOrdSort()
        {
            stack mut Item[5] items =
            {
                new Item() { Key = 5, Value = 1 },
                new Item() { Key = 4, Value = 2 },
                new Item() { Key = 3, Value = 3 },
                new Item() { Key = 2, Value = 4 },
                new Item() { Key = 1, Value = 5 }
            };

            Sort<Item>(items);
            return items[0].Key == 1
                && items[1].Key == 2
                && items[2].Key == 3
                && items[3].Key == 4
                && items[4].Key == 5;
        }

        export unsafe fn i32[min max] main()
        {
            if (!CheckFixedArraySort())
            {
                return 1;
            }

            if (!CheckListSliceSort())
            {
                return 2;
            }

            if (!CheckAggregateSort())
            {
                return 3;
            }

            if (!CheckAggregateOrdSort())
            {
                return 4;
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

        fn bool Ok(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return true;
                case MemoryStatus.Err(var error):
                    return false;
            }
        }

        fn bool TooLarge(MemoryStatus status)
        {
            switch (status)
            {
                case MemoryStatus.Ok:
                    return false;
                case MemoryStatus.Err(var error):
                    return error == MemoryError.TooLarge;
            }
        }

        fn void Bump(i32[min max] value)
        {
            DropCounter = DropCounter + value;
            return;
        }

        struct Resource
        {
            i32[min max] Value;

            drop
            {
                Bump(self.Value);
            }
        }

        fn bool CheckListParity()
        {
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.List<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(3)) || !Ok(experimental.Reserve(3)))
            {
                return false;
            }

            for willexit (stack mut u8[0 40] i = 0; i < 40; i += 1)
            {
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])((i * 3) + 1);
                if (!Ok(stable.Push(value)) || !Ok(experimental.Push(value)))
                {
                    return false;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count())
            {
                return false;
            }

            stable.GetMut(5) = 55;
            experimental.GetMut(5) = 55;
            stable.AsMutableSlice()[7] = 77;
            experimental.AsMutableSlice()[7] = 77;

            for willexit (stack mut u8[0 40] i = 0; i < 40; i += 1)
            {
                if (stable.Get(i) != experimental.Get(i) || stable.AsSlice()[i] != experimental.AsSlice()[i])
                {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 20] i = 0; i < 20; i += 1)
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue) || stableValue != experimentalValue)
                {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            if (stable.Count() != 20 || experimental.Count() != 20 || checksum != 1790)
            {
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

        fn bool CheckStackParity()
        {
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Stack<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(2)) || !Ok(experimental.Reserve(2)))
            {
                return false;
            }

            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1)
            {
                if (!Ok(stable.Push(i)) || !Ok(experimental.Push(i)) || stable.Peek() != experimental.Peek())
                {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            while willexit (!experimental.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryPop(stableValue) || !experimental.TryPop(experimentalValue) || stableValue != experimentalValue)
                {
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

        fn bool CheckQueueParity()
        {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(4)) || !Ok(experimental.Reserve(4)))
            {
                return false;
            }

            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i)) || stable.Peek() != experimental.Peek())
                {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 16] i = 0; i < 16; i += 1)
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue)
                {
                    return false;
                }

                checksum += (i64[min max])experimentalValue;
            }

            for willexit (stack mut u8[0 72] i = 48; i < 72; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(experimental.Enqueue(i)))
                {
                    return false;
                }
            }

            while willexit (!experimental.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
                if (!stable.TryDequeue(stableValue) || !experimental.TryDequeue(experimentalValue) || stableValue != experimentalValue)
                {
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

        fn bool CheckRingQueueParity()
        {
            stack mut System.Collections.Queue<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.RingQueue<u32[0 2 ** 31 - 1]> ring = new();
            for willexit (stack mut u8[0 32] i = 0; i < 32; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i)))
                {
                    return false;
                }
            }

            stack mut i64[min max] checksum = 0;
            for willexit (stack mut u8[0 12] i = 0; i < 12; i += 1)
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue)
                {
                    return false;
                }

                checksum += (i64[min max])ringValue;
            }

            for willexit (stack mut u8[0 64] i = 32; i < 64; i += 1)
            {
                if (!Ok(stable.Enqueue(i)) || !Ok(ring.Enqueue(i)))
                {
                    return false;
                }
            }

            while willexit (!ring.IsEmpty())
            {
                stack mut u32[0 2 ** 31 - 1] stableValue = 0;
                stack mut u32[0 2 ** 31 - 1] ringValue = 0;
                if (!stable.TryDequeue(stableValue) || !ring.TryDequeue(ringValue) || stableValue != ringValue)
                {
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

        fn bool CheckLinkedListParity()
        {
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.LinkedList<u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.ReserveNodes(3)) || !Ok(experimental.ReserveNodes(3)))
            {
                return false;
            }

            if (!Ok(stable.AddLast(10)) || !Ok(experimental.AddLast(10))
                || !Ok(stable.AddLast(20)) || !Ok(experimental.AddLast(20))
                || !Ok(stable.AddFirst(5)) || !Ok(experimental.AddFirst(5))
                || !Ok(stable.AddLast(30)) || !Ok(experimental.AddLast(30)))
                {
                    return false;
            }

            stack mut u32[0 2 ** 31 - 1] stableValue = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalValue = 0;
            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 5 || experimentalValue != 5)
            {
                return false;
            }

            if (!stable.TryRemoveLast(stableValue) || !experimental.TryRemoveLast(experimentalValue) || stableValue != 30 || experimentalValue != 30)
            {
                return false;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 10 || experimentalValue != 10)
            {
                return false;
            }

            if (!stable.TryRemoveFirst(stableValue) || !experimental.TryRemoveFirst(experimentalValue) || stableValue != 20 || experimentalValue != 20)
            {
                return false;
            }

            if (!stable.IsEmpty() || !experimental.IsEmpty())
            {
                return false;
            }

            for willexit (stack mut u8[0 24] i = 0; i < 24; i += 1)
            {
                if (!Ok(stable.AddLast(i)) || !Ok(experimental.AddLast(i)))
                {
                    return false;
                }
            }

            stable.Clear();
            experimental.Clear();
            return stable.Count() == 0 && experimental.Count() == 0;
        }

        fn bool CheckDictionaryParity()
        {
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> stable = new();
            stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> experimental = new();
            if (!Ok(stable.Reserve(5)) || !Ok(experimental.Reserve(5)))
            {
                return false;
            }

            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i + 100);
                if (!Ok(stable.Set(key, value)) || !Ok(experimental.Set(key, value)))
                {
                    return false;
                }
            }

            if (stable.Count() != experimental.Count() || stable.Capacity() < stable.Count() || experimental.Capacity() < experimental.Count())
            {
                return false;
            }

            stack mut i64[min max] checksum = 0;
            stack mut u32[0 2 ** 31 - 1] stableFound = 0;
            stack mut u32[0 2 ** 31 - 1] experimentalFound = 0;
            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 2);
                if (!stable.TryGet(key, stableFound) || !experimental.TryGet(key, experimentalFound) || stableFound != experimentalFound)
                {
                    return false;
                }

                checksum += (i64[min max])experimentalFound;
            }

            if (checksum != 5928)
            {
                return false;
            }

            stack u32[0 2 ** 31 - 1] updateKey = 20;
            if (!Ok(stable.Set(updateKey, 999)) || !Ok(experimental.Set(updateKey, 999))
                || stable.Count() != experimental.Count()
                || !stable.TryGet(updateKey, stableFound)
                || !experimental.TryGet(updateKey, experimentalFound)
                || stableFound != experimentalFound
                || experimentalFound != 999)
                {
                    return false;
            }

            for willexit (stack mut u8[0 12] i = 0; i < 12; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = (u32[0 2 ** 31 - 1])(i * 4);
                if (!stable.Remove(key) || !experimental.Remove(key) || stable.ContainsKey(key) || experimental.ContainsKey(key))
                {
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
                || stableFound != experimentalFound)
                {
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

        fn bool CheckOwnedValueCleanup()
        {
            DropCounter = 0;
            {
                stack mut System.Collections.List<Resource> values = new();
                if (!Ok(values.Push(new Resource()
                {
                    Value = 1
                }
                )) || !Ok(values.Push(new Resource()
                {
                    Value = 2
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 3)
            {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> values = new();
                if (!Ok(values.Push(new Resource()
                {
                    Value = 3
                }
                )) || !Ok(values.Push(new Resource()
                {
                    Value = 4
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 10)
            {
                return false;
            }

            {
                stack mut System.Collections.Stack<Resource> values = new();
                if (!Ok(values.Push(new Resource()
                {
                    Value = 5
                }
                )) || !Ok(values.Push(new Resource()
                {
                    Value = 6
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 21)
            {
                return false;
            }

            {
                stack mut System.Collections.Stack<Resource> values = new();
                if (!Ok(values.Push(new Resource()
                {
                    Value = 7
                }
                )) || !Ok(values.Push(new Resource()
                {
                    Value = 8
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 36)
            {
                return false;
            }

            {
                stack mut System.Collections.Queue<Resource> values = new();
                if (!Ok(values.Enqueue(new Resource()
                {
                    Value = 9
                }
                )) || !Ok(values.Enqueue(new Resource()
                {
                    Value = 10
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 55)
            {
                return false;
            }

            {
                stack mut System.Collections.Queue<Resource> values = new();
                if (!Ok(values.Enqueue(new Resource()
                {
                    Value = 11
                }
                )) || !Ok(values.Enqueue(new Resource()
                {
                    Value = 12
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 78)
            {
                return false;
            }

            {
                stack mut System.Collections.LinkedList<Resource> values = new();
                if (!Ok(values.AddLast(new Resource()
                {
                    Value = 13
                }
                )) || !Ok(values.AddFirst(new Resource()
                {
                    Value = 14
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 105)
            {
                return false;
            }

            {
                stack mut System.Collections.LinkedList<Resource> values = new();
                if (!Ok(values.AddLast(new Resource()
                {
                    Value = 15
                }
                )) || !Ok(values.AddFirst(new Resource()
                {
                    Value = 16
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 136)
            {
                return false;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> values = new();
                stack u32[0 2 ** 31 - 1] one = 1;
                stack u32[0 2 ** 31 - 1] two = 2;
                if (!Ok(values.Set(one, new Resource()
                {
                    Value = 17
                }
                )) || !Ok(values.Set(two, new Resource()
                {
                    Value = 18
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 171)
            {
                return false;
            }

            {
                stack mut System.Collections.Dictionary<u32[0 2 ** 31 - 1], Resource> values = new();
                stack u32[0 2 ** 31 - 1] three = 3;
                stack u32[0 2 ** 31 - 1] four = 4;
                if (!Ok(values.Set(three, new Resource()
                {
                    Value = 19
                }
                )) || !Ok(values.Set(four, new Resource()
                {
                    Value = 20
                }
                )))
                {
                    return false;
                }

                values.Clear();
            }

            if (DropCounter != 210)
            {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> stableScoped = new();
                if (!Ok(stableScoped.Push(new Resource()
                {
                    Value = 21
                }
                )) || !Ok(stableScoped.Push(new Resource()
                {
                    Value = 22
                }
                )))
                {
                    return false;
                }
            }

            if (DropCounter != 253)
            {
                return false;
            }

            {
                stack mut System.Collections.List<Resource> experimentalScoped = new();
                if (!Ok(experimentalScoped.Push(new Resource()
                {
                    Value = 23
                }
                )) || !Ok(experimentalScoped.Push(new Resource()
                {
                    Value = 24
                }
                )))
                {
                    return false;
                }
            }

            return DropCounter == 300;
        }

        export unsafe fn i32[min max] main()
        {
            if (!CheckListParity())
            {
                return 1;
            }

            if (!CheckStackParity())
            {
                return 2;
            }

            if (!CheckQueueParity())
            {
                return 3;
            }

            if (!CheckRingQueueParity())
            {
                return 4;
            }

            if (!CheckLinkedListParity())
            {
                return 5;
            }

            if (!CheckDictionaryParity())
            {
                return 6;
            }

            if (!CheckOwnedValueCleanup())
            {
                return 7;
            }

            return 0;
        }
        """;

    [Fact]
    public void StdLibSourceSortByUsesInlineComparatorWithoutRuntimeClosureOrAllocation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibSortByLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                module Demo

                finite law Ordering CompareU32(borrow u32[0 max] left, borrow u32[0 max] right)
                    where overlap(left, right)
                {
                    if (left < right)
                    {
                        return Ordering.Less;
                    }

                    if (left > right)
                    {
                        return Ordering.Greater;
                    }

                    return Ordering.Equal;
                }

                fn u32[0 max] SortFixed()
                {
                    stack mut u32[0 max][8] values = { 9, 1, 5, 3, 5, 2, 8, 0 };
                    SortBy<u32[0 max]>(values, CompareU32);
                    return values[0] + values[7];
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        var sortFixedBody = ExtractDefinedFunctionText(llvm.Text, "@SortFixed(");
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_alloc", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc %", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc void %", sortFixedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceSortUsesOrdContractWithoutRuntimeClosureOrAllocation()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibSortOrdLowering.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                module Demo

                struct Item : Ord
                {
                    u32[0 max] Key;

                    finite law Ordering Compare(borrow Item left, borrow Item right)
                        where overlap(left, right)
                    {
                        if (left.Key < right.Key)
                        {
                            return Ordering.Less;
                        }

                        if (left.Key > right.Key)
                        {
                            return Ordering.Greater;
                        }

                        return Ordering.Equal;
                    }
                }

                fn u32[0 max] SortFixed()
                {
                    stack mut Item[8] values =
                    {
                        new Item() { Key = 9 },
                        new Item() { Key = 1 },
                        new Item() { Key = 5 },
                        new Item() { Key = 3 },
                        new Item() { Key = 5 },
                        new Item() { Key = 2 },
                        new Item() { Key = 8 },
                        new Item() { Key = 0 }
                    };

                    Sort<Item>(values);
                    return values[0].Key + values[7].Key;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        var sortFixedBody = ExtractDefinedFunctionText(llvm.Text, "@SortFixed(");
        Assert.DoesNotContain("; LLVM body emission fallback", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@realloc(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@free(", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_alloc", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_realloc", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("@__stark_runtime_free", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("insertvalue { ptr, ptr }", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("extractvalue { ptr, ptr }", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc %", sortFixedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc void %", sortFixedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceSortRequiresOrdConformance()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibSortOrdDiagnostics.stark");
        var missingResult = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                module Demo

                struct Box
                {
                    u32[0 max] Key;
                }

                fn void SortBoxes(mut borrow Box[] boxes)
                {
                    Sort<Box>(boxes);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "instantiation-ownership"));

        Assert.False(missingResult.Succeeded);
        Assert.Contains(
            missingResult.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3034"
                && diagnostic.Message.Contains("does not satisfy the 'System.Collections.Ord' bound", StringComparison.Ordinal));

        var incompatibleResult = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                module Demo

                struct Box : Ord
                {
                    u32[0 max] Key;

                    finite law i32[min max] Compare(borrow Box left, borrow Box right)
                        where overlap(left, right)
                    {
                        return 0;
                    }
                }

                fn void SortBoxes(mut borrow Box[] boxes)
                {
                    Sort<Box>(boxes);
                    return;
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "semantic-validate"));

        Assert.False(incompatibleResult.Succeeded);
        Assert.Contains(
            incompatibleResult.Diagnostics,
            static diagnostic => diagnostic.Code == "STK3033"
                && diagnostic.Message.Contains("Box.Compare", StringComparison.Ordinal)
                && diagnostic.Message.Contains("System.Collections.Ord.Compare", StringComparison.Ordinal));
    }

    [Fact]
    public void StdLibSourceHashSetGrowthLowersThroughDictionaryFastPath()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibHashSetGrowth.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool GrowHashSet()
                {
                    stack mut HashSet<u32[0 2 ** 31 - 1]> set = new();
                    stack mut u32[0 2 ** 31 - 1] index = 0;
                    while willexit (index < 9)
                    {
                        if (!Ok(set.Add(index)))
                        {
                            return false;
                        }

                        index += 1;
                    }

                    stack u32[0 2 ** 31 - 1] lookupKey = 4;
                    return set.Capacity() >= 16
                        && set.Contains(lookupKey);
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
        var findIndexBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_HashSet_FindIndex__u32_0_2147483647",
            "Expected integer HashSet.FindIndex specialization to be emitted.");
        Assert.DoesNotContain(" srem i64 ", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__", findIndexBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StdLibSourceHashSetCustomKeysUseExplicitStaticHashAndEqualsContract()
    {
        var repositoryRoot = FindRepositoryRoot();
        var sourceRoot = Path.Combine(repositoryRoot, "stdlib", "src");
        var appPath = Path.Combine(repositoryRoot, "tests", "tmp", "StdLibHashSetCustomKey.stark");
        var result = DefaultCompilerPipeline.Create().Run(
            new CompilationInput(
                """
                import System.Collections
                import System.Memory
                module Demo

                struct Symbol
                {
                    u32[0 max] Id;

                    static finite law u64[0 max] Hash(borrow Symbol value)
                    {
                        return (u64[0 max])value.Id;
                    }

                    static finite law bool Equals(borrow Symbol left, borrow Symbol right)
                        where overlap(left, right)
                    {
                        return left.Id == right.Id;
                    }
                }

                fn bool Ok(MemoryStatus status)
                {
                    switch (status)
                    {
                        case MemoryStatus.Ok:
                            return true;
                        case MemoryStatus.Err(var error):
                            return false;
                    }
                }

                fn bool UseCustomKeyHashSet()
                {
                    stack mut HashSet<Symbol> set = new();
                    stack Symbol first = new()
                    {
                        Id = 7
                    };
                    stack Symbol sameKey = new()
                    {
                        Id = 7
                    };
                    stack Symbol missing = new()
                    {
                        Id = 9
                    };
                    if (!Ok(set.Add(first)))
                    {
                        return false;
                    }

                    return set.Contains(sameKey)
                        && !set.Contains(missing);
                }
                """,
                appPath),
            new CompilerOptions(
                ModuleResolver: new FileSystemModuleResolver(sourceRoot),
                StopAfterPassId: "emit-llvm"));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString())));
        Assert.True(result.Artifacts.TryGet(CompilerArtifactKeys.LlvmIrModule, out LlvmIrModule? llvm));
        Assert.NotNull(llvm);
        Assert.Contains("define fastcc noundef range(i64 0, 4294967296) i64 @Symbol_Hash(ptr noundef nonnull noalias readonly captures(none) dereferenceable(4) align 4", llvm.Text, StringComparison.Ordinal);
        Assert.Contains("define fastcc noundef i1 @Symbol_Equals(ptr noundef nonnull noalias readonly captures(none) dereferenceable(4) align 4", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @Symbol_Hash", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @Symbol_Equals", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i64 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Hash__Symbol", llvm.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("call fastcc i1 @__stark_mono_fn_System_Collections__System_Collections_DictionaryKey_Equals__Symbol", llvm.Text, StringComparison.Ordinal);

        var containsBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef i1 @__stark_mono_fn_System_Collections__System_Collections_HashSet_Contains__Symbol",
            "Expected custom-key HashSet.Contains specialization to be emitted.");
        Assert.DoesNotContain("DictionaryKey_Hash", containsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", containsBody, StringComparison.Ordinal);
        var findIndexBody = ExtractDefinedFunctionText(
            llvm.Text,
            "define linkonce_odr dso_local fastcc noundef range(i64 0, -9223372036854775808) i64 @__stark_mono_fn_System_Collections__System_Collections_HashSet_FindIndex__Symbol",
            "Expected custom-key HashSet.FindIndex specialization to be emitted.");
        Assert.Contains("load i32", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("zext i32", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("icmp eq i32", findIndexBody, StringComparison.Ordinal);
        Assert.Contains("!tbaa", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Hash", findIndexBody, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKey_Equals", findIndexBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SourceStdLibHashSetExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-hash-set-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, PromotedHashSetProgram);

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
    public async Task SourceStdLibDeterministicSortingExecutableRuns()
    {
        if (!NativeToolchain.TryDetectDefaultTargetInfo(out var targetInfo))
        {
            return;
        }

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
        var tempDirectory = Directory.CreateTempSubdirectory("stark-stdlib-deterministic-sorting-");
        var appPath = Path.Combine(tempDirectory.FullName, "App.stark");
        var outputPath = Path.Combine(tempDirectory.FullName, OperatingSystem.IsWindows() ? "App.exe" : "app");

        try
        {
            await File.WriteAllTextAsync(appPath, DeterministicSortingProgram);

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

        var sourceRoot = await SharedStdlibPackage.GetDirectoryAsync();
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
