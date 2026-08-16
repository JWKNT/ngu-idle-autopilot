.\injector\smi.exe inject -p NGUIdle -a .\injector\NGUInjector.dll -n NGUInjector -c Loader -m Init
REM FILE PURPOSE
REM
REM Legacy Windows convenience wrapper for injecting the compiled NGUInjector assembly. The macOS
REM CrossOver deployment uses repository run.command instead. Keep exact injector class/method names
REM aligned with AutopilotLoader.cs; this script owns no build or gameplay policy.
