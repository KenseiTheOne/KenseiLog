# Kensei Log

Tagged logging for Unity with separate dev and prod channels. Dev calls carry
`[Conditional]`, so the compiler removes them, and every argument expression they were built
from, outside the editor and development builds.

The full guide is [README.md](../README.md) in the package root, and it is the one kept up to
date. This page is the short version and the map.

![The in-game overlay open over a running build](images/overlay-open.png)

## Where to start

| If you want to | Read |
| --- | --- |
| Write your first log line | [Usage](../README.md#usage) |
| Know which channel a line belongs in | [The two channels](../README.md#the-two-channels) |
| Read logs in the editor | [The window](../README.md#the-window) |
| Read logs from a build a tester sent you | [Logs from a build](../README.md#logs-from-a-build) |
| Read logs on the device as it runs | [Logs on the device](../README.md#logs-on-the-device-while-playing) |
| Change the defaults | [Configuration](../README.md#configuration) |
| Write a sink, or drive the viewers yourself | [Driving it from your own code](../README.md#driving-it-from-your-own-code) |

## The pieces

**`Log`** - six methods: `Info`, `Warning`, `Error` on the prod channel, `DevInfo`,
`DevWarning`, `DevError` on the dev one. A tag is the first argument, or absent, in which case the record
lands under `Untagged`.

**`Logger`** - the same six with the tag fixed, for a file that always logs under one.

**`LogCore`** - the pipeline. It builds records and hands them to the registered sinks, and it
is where `Configure` applies a `LogConfig`.

**Sinks** - `UnityConsoleSink` mirrors into the console, `FileSink` writes a rolling JSONL
file, `MemorySink` keeps recent records in a ring for something to display, and `ILogSink`
takes one of your own.

**The window** - Window → Kensei → Logs. Filter tabs, a tag tree, collapse, and it opens a
JSONL file from a build as though it were a live run.

**The overlay** - a bubble on the device that expands into a viewer, off until
`LogConfig.ShowOverlay` turns it on.

## Working on the package

The checks and the tooling behind the screenshots live in `Tests~/`, with instructions in
`Tests~/README.md`. The folder's `~` keeps Unity from importing it, so nothing there compiles
into the package.
