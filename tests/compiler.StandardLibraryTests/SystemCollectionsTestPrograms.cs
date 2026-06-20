namespace compiler.StandardLibraryTests;

internal static class SystemCollectionsTestPrograms
{
    internal const string CollectionsGrowthMoveDropProgram = """
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

        fn bool ConsumeList(List<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            return values.Count() == expected && values.Capacity() >= expected;
        }

        fn bool ConsumeStack(Stack<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            return values.Count() == expected && values.Peek() == 79;
        }

        fn bool ConsumeQueue(Queue<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            return values.Count() == expected && values.Peek() == 0;
        }

        fn bool ConsumeLinkedList(LinkedList<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            return values.Count() == expected;
        }

        fn bool ConsumeDictionary(Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            stack u32[0 2 ** 31 - 1] key = 17;
            stack mut u32[0 2 ** 31 - 1] found = 0;
            return values.Count() == expected
                && IsPowerOfTwo(values.Capacity())
                && values.ContainsKey(key)
                && values.TryGet(key, found)
                && found == 34;
        }

        fn bool ConsumeHashSet(HashSet<u32[0 2 ** 31 - 1]> values, u64[0 2 ** 63 - 1] expected)
        {
            stack u32[0 2 ** 31 - 1] key = 17;
            return values.Count() == expected
                && IsPowerOfTwo(values.Capacity())
                && values.Contains(key);
        }

        export unsafe fn i32[min max] main()
        {
            stack mut List<u32[0 2 ** 31 - 1]> list = new();
            for willexit (stack mut u8[0 96] i = 0; i < 96; i += 1)
            {
                if (!Ok(list.Push(i)))
                {
                    return 1;
                }
            }

            if (!ConsumeList(list, 96))
            {
                return 2;
            }

            stack mut Stack<u32[0 2 ** 31 - 1]> stackValues = new();
            for willexit (stack mut u8[0 80] i = 0; i < 80; i += 1)
            {
                if (!Ok(stackValues.Push(i)))
                {
                    return 3;
                }
            }

            if (!ConsumeStack(stackValues, 80))
            {
                return 4;
            }

            stack mut Queue<u32[0 2 ** 31 - 1]> queue = new();
            for willexit (stack mut u8[0 96] i = 0; i < 96; i += 1)
            {
                if (!Ok(queue.Enqueue(i)))
                {
                    return 5;
                }
            }

            if (!ConsumeQueue(queue, 96))
            {
                return 6;
            }

            stack mut LinkedList<u32[0 2 ** 31 - 1]> linked = new();
            for willexit (stack mut u8[0 48] i = 0; i < 48; i += 1)
            {
                if (!Ok(linked.AddLast(i)))
                {
                    return 7;
                }
            }

            if (!ConsumeLinkedList(linked, 48))
            {
                return 8;
            }

            stack mut Dictionary<u32[0 2 ** 31 - 1], u32[0 2 ** 31 - 1]> dictionary = new();
            if (!Ok(dictionary.Reserve(3)) || !IsPowerOfTwo(dictionary.Capacity()))
            {
                return 9;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                stack u32[0 2 ** 31 - 1] value = (u32[0 2 ** 31 - 1])(i * 2);
                if (!Ok(dictionary.Set(key, value)))
                {
                    return 9;
                }

                if (!IsPowerOfTwo(dictionary.Capacity()))
                {
                    return 9;
                }

                if (i == 4 && dictionary.Capacity() < 16)
                {
                    return 9;
                }
            }

            if (!ConsumeDictionary(dictionary, 64))
            {
                return 10;
            }

            stack mut HashSet<u32[0 2 ** 31 - 1]> set = new();
            if (!Ok(set.Reserve(3)) || !IsPowerOfTwo(set.Capacity()))
            {
                return 11;
            }

            for willexit (stack mut u8[0 64] i = 0; i < 64; i += 1)
            {
                stack u32[0 2 ** 31 - 1] key = i;
                if (!Ok(set.Add(key)))
                {
                    return 11;
                }

                if (!IsPowerOfTwo(set.Capacity()))
                {
                    return 11;
                }
            }

            if (!ConsumeHashSet(set, 64))
            {
                return 12;
            }

            return 0;
        }
        """;
}
