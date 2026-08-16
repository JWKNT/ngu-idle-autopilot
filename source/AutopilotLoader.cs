using UnityEngine;

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
