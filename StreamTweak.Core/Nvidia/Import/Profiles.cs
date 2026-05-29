// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Import/Profiles.cs
// Root of a .nip file: <ArrayOfProfile>.
using System;
using System.Collections.Generic;

namespace StreamTweak.Nvidia.Import
{
    [Serializable]
    public class Profiles : List<Profile>
    {

    }
}
