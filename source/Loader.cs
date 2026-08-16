using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NGUInjector
{
    public class Loader
    {
        private static GameObject _load;
        public static void Init()
        {
            _load = new GameObject();
            _load.AddComponent<Main>();
            Object.DontDestroyOnLoad(_load);
        }

        public static void Unload()
        {
            _Unload();
        }

        private static void _Unload()
        {
            if (Main.reference != null)
                Main.reference.Unload();
            if (_load == null) return;
            _load.SetActive(false);
            Object.Destroy(_load);
            _load = null;
        }
    }
}
