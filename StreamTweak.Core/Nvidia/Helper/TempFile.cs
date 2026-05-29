// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Helper/TempFile.cs
// Only the namespace changed (→ StreamTweak.Nvidia.Helper).
using System;
using System.IO;

namespace StreamTweak.Nvidia.Helper
{
    public static class TempFile
    {
        public static string GetTempFileName()
        {
            while (true)
            {
                var tempFile = GenerateTempFileName();
                if (!File.Exists(tempFile))
                    return tempFile;
            }
        }

        private static string GenerateTempFileName()
        {
            return Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString().Replace("-", ""));
        }

    }
}
