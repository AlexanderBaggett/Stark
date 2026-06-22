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

`SQLiteSmoke.stark` remains a smaller binding smoke test:

```bash
./stark examples/sqlite/SQLiteSmoke.stark --emit-exe -I vendor/dist -o /tmp/stark-sqlite-smoke
/tmp/stark-sqlite-smoke
```
