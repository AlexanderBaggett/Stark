# SQLite Examples

`TaskReport.stark` uses the bundled `Vendor.SQLite` binding against the system
SQLite library. It opens an in-memory database, creates a task table, reuses a
prepared insert statement, binds integer and text parameters, queries aggregate
counts, reads text back, and prints a short report.

Build the vendor package first:

```bash
bash vendor/build-sqlite-package.sh
```

Then run the example through the examples solution:

```bash
cd examples
dotnet run --project ../src/compiler.csproj -- run sqlite
```

Expected output:

```text
SQLite task report: 3 tasks, 2 complete, priority sum 10
Top pending task:
document-usage
```

You can also compile it directly:

```bash
./stark examples/sqlite/TaskReport.stark --emit-exe -I vendor/dist -I stdlib/src -o /tmp/stark-sqlite-task-report
/tmp/stark-sqlite-task-report
```

`SQLiteInMemoryQueries.stark` is a smaller in-memory query example:

```bash
./stark examples/sqlite/SQLiteInMemoryQueries.stark --emit-exe -I vendor/dist -o /tmp/stark-sqlite-in-memory-queries
/tmp/stark-sqlite-in-memory-queries
```

`SQLiteCallbacks.stark` registers a Stark-authored scalar SQL function and a
custom collation callback:

```bash
./stark examples/sqlite/SQLiteCallbacks.stark --emit-exe -I vendor/dist -I stdlib/src -o /tmp/stark-sqlite-callbacks
/tmp/stark-sqlite-callbacks
```

`SQLiteBinaryData.stark` binds a blob, then reads the blob and text columns back
through the owned byte-copy APIs:

```bash
./stark examples/sqlite/SQLiteBinaryData.stark --emit-exe -I vendor/dist -I stdlib/src -o /tmp/stark-sqlite-binary-data
/tmp/stark-sqlite-binary-data
```

`SQLiteSnapshots.stark` demonstrates the optional snapshot wrapper behavior. It
works on lean SQLite builds where the snapshot extension is absent; on builds
compiled with `SQLITE_ENABLE_SNAPSHOT`, it writes a small WAL database, opens a
historical snapshot, and verifies the older row value:

```bash
./stark examples/sqlite/SQLiteSnapshots.stark --emit-exe -I vendor/dist -I stdlib/src -o /tmp/stark-sqlite-snapshots
/tmp/stark-sqlite-snapshots
```
