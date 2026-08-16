# Injection transport (not bot source)

This directory is expected to contain the SharpMonoInjector console artifacts used by `run.command` and `stop.command`:

```text
injector/
  smi.exe
  SharpMonoInjector.dll
```

The binary files are intentionally ignored. Obtain/build them from the MIT-licensed [warbler/SharpMonoInjector](https://github.com/warbler/SharpMonoInjector) project and verify them independently.

SharpMonoInjector is only the process transport. The actual bot is built from `source/` into `NGUIdleAutopilot.dll`.

