// Ported from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/DrsSessionScope.cs
// Changes: namespace → StreamTweak.Nvidia.Drs; NVAPI types from
//   StreamTweak.Nvidia.Native. Logic unchanged.
//
// Holds a single global DRS session (lock-guarded, reentrant on the same thread)
// so repeated reads reuse it. NonGlobalDrsSession is used when a fresh dedicated
// session is required.
using StreamTweak.Nvidia.Native;
using System;
using nvw = StreamTweak.Nvidia.Native.NvapiDrsWrapper;

namespace StreamTweak.Nvidia.Drs
{
    public class DrsSessionScope
    {

        public static volatile IntPtr GlobalSession;

        public static volatile bool HoldSession = true;

        private static object _Sync = new object();


        public static T DrsSession<T>(Func<IntPtr, T> action, bool forceNonGlobalSession = false, bool preventLoadSettings = false)
        {
            lock (_Sync)
            {
                if (!HoldSession || forceNonGlobalSession)
                    return NonGlobalDrsSession<T>(action, preventLoadSettings);


                if (GlobalSession == IntPtr.Zero)
                {

#pragma warning disable CS0420
                    var csRes = nvw.Instance.DRS_CreateSession(ref GlobalSession);
#pragma warning restore CS0420

                    if (csRes != NvAPI_Status.NVAPI_OK)
                        throw new NvapiException("DRS_CreateSession", csRes);

                    if (!preventLoadSettings)
                    {
                        var nvRes = nvw.Instance.DRS_LoadSettings(GlobalSession);
                        if (nvRes != NvAPI_Status.NVAPI_OK)
                            throw new NvapiException("DRS_LoadSettings", nvRes);
                    }
                }
            }

            if (GlobalSession != IntPtr.Zero)
            {
                return action(GlobalSession);
            }

            throw new Exception(nameof(GlobalSession) + " is Zero!");
        }

        public static void DestroyGlobalSession()
        {
            lock (_Sync)
            {
                if (GlobalSession != IntPtr.Zero)
                {
                    var csRes = nvw.Instance.DRS_DestroySession(GlobalSession);
                    GlobalSession = IntPtr.Zero;
                }
            }
        }

        private static T NonGlobalDrsSession<T>(Func<IntPtr, T> action, bool preventLoadSettings = false)
        {
            IntPtr hSession = IntPtr.Zero;
            var csRes = nvw.Instance.DRS_CreateSession(ref hSession);
            if (csRes != NvAPI_Status.NVAPI_OK)
                throw new NvapiException("DRS_CreateSession", csRes);

            try
            {
                if (!preventLoadSettings)
                {
                    var nvRes = nvw.Instance.DRS_LoadSettings(hSession);
                    if (nvRes != NvAPI_Status.NVAPI_OK)
                        throw new NvapiException("DRS_LoadSettings", nvRes);
                }

                return action(hSession);
            }
            finally
            {
                var nvRes = nvw.Instance.DRS_DestroySession(hSession);
                if (nvRes != NvAPI_Status.NVAPI_OK)
                    throw new NvapiException("DRS_DestroySession", nvRes);
            }

        }


    }
}
