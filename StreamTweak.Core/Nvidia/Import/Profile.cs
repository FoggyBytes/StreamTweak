// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Import/Profile.cs
// One <Profile> element of a .nip file. For the global driver profile,
// ProfileName is empty and Executeables is empty.
using System;
using System.Collections.Generic;

namespace StreamTweak.Nvidia.Import
{
    [Serializable]
    public class Profile
    {
        public string ProfileName = "";
        public List<string> Executeables = new List<string>();
        public List<ProfileSetting> Settings = new List<ProfileSetting>();
    }
}
