using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

/*
FILE PURPOSE

This file owns assembly lifecycle inside Unity: injection creates the single named persistent Main
component and ejection destroys it while invalidating every outstanding execution lease. Inputs are
the injector's Init/Unload calls and Unity's object registry; output is exactly one scheduler host.
Exactly-one-host and lease invalidation prevent duplicated timers, stale callbacks, and double
mutations. Gameplay policy, deployment selection, and manager mechanics do not belong here.
*/
namespace NGUInjector
{
    public class Loader
    {
        private const string HostName = "NGU Autopilot";
        private static GameObject _load;
        public static void Init()
        {
            if (_load != null || GameObject.Find(HostName) != null)
                throw new InvalidOperationException("NGU Autopilot is already injected; refusing to create a second scheduler host.");

            NGUInjector.Autopilot.ExecutionSafety.Invalidate("injector lifecycle Init");
            _load = new GameObject(HostName);
            _load.AddComponent<Main>();
            Object.DontDestroyOnLoad(_load);
        }

        public static void Unload()
        {
            _Unload();
        }

        private static void _Unload()
        {
            NGUInjector.Autopilot.ExecutionSafety.Invalidate("injector lifecycle Unload");
            if (Main.reference != null)
                Main.reference.Unload();
            if (_load == null) return;
            _load.SetActive(false);
            Object.Destroy(_load);
            _load = null;
        }
    }
}
