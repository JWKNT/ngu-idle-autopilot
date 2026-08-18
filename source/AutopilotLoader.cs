/*
FILE PURPOSE

This is the stable injector-facing compatibility entrypoint. SharpMonoInjector calls these exact
Init/Unload names, which delegate to the one authoritative NGUInjector.Loader lifecycle and its
execution-lease invalidation. Inputs and outputs are injector lifecycle calls only. It owns no
host reference, gameplay policy, disk selection, or native mutation and must never create a second
automation host; duplicate-host rejection and teardown postconditions remain Loader's job.
*/
namespace NGUAutopilot
{
    public static class Loader
    {
        public static void Init()
        {
            NGUInjector.Loader.Init();
        }

        public static void Unload()
        {
            NGUInjector.Loader.Unload();
        }
    }
}
