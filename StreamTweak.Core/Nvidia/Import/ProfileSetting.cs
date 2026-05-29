// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Import/ProfileSetting.cs
// This is one <ProfileSetting> element of a .nip file.
using System;
using System.Xml.Serialization;

namespace StreamTweak.Nvidia.Import
{
    [Serializable]
    public class ProfileSetting
    {
        public string SettingNameInfo = "";

        [XmlElement(ElementName = "SettingID")]
        public uint SettingId = 0;

        public string SettingValue = "0";

        public SettingValueType ValueType = SettingValueType.Dword;
    }
}
