using UnityEngine;

/*
FILE PURPOSE

This is the stable injector-facing compatibility entrypoint. SharpMonoInjector calls these exact
Init/Unload names, which delegate to the real NGUInjector.Loader lifecycle. Keep this boundary
dependency-free: it owns no gameplay policy and must never create a second automation host.
*/
namespace NGUAutopilot
{
    public static class Loader
    {
        private static GameObject _host;

        public static void Init()
        {
            _host = new GameObject("NGU Autopilot");
            _host.AddComponent<NGUInjector.Main>();
            Object.DontDestroyOnLoad(_host);
        }

        public static void Unload()
        {
            if (NGUInjector.Main.reference != null)
                NGUInjector.Main.reference.Unload();
            if (_host == null) return;
            _host.SetActive(false);
            Object.Destroy(_host);
            _host = null;
        }
    }
}
