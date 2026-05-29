// Ported verbatim from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/Import/ImportExportUitl.cs
// Changes: namespace → StreamTweak.Nvidia.Import; class renamed
//   ImportExportUitl → ImportExportUtil (typo fix); NVAPI types from
//   StreamTweak.Nvidia.Native. Logic is unchanged.
using StreamTweak.Nvidia.Native;
using System;

namespace StreamTweak.Nvidia.Import
{
    public class ImportExportUtil
    {
        public static bool AreDrsSettingEqualToProfileSetting(NVDRS_SETTING drsSetting, ProfileSetting profileSetting)
        {
            var profileSettingCompare = ConvertDrsSettingToProfileSetting(drsSetting);
            return profileSetting.SettingValue.Equals(profileSettingCompare.SettingValue);
        }

        public static ProfileSetting ConvertDrsSettingToProfileSetting(NVDRS_SETTING setting)
        {
            return new ProfileSetting
            {
                SettingId = setting.settingId,
                SettingNameInfo = setting.settingName,
                SettingValue = ConvertSettingValueToString(setting),
                ValueType = MapValueType(setting.settingType),
            };
        }

        /// <summary>
        /// Renders the PREDEFINED (driver default) value of a setting as a string,
        /// using the same formatting as the current-value conversion. Caller should
        /// only use this when setting.isPredefinedValid == 1.
        /// (StreamTweak addition — not in upstream NVPI.)
        /// </summary>
        public static string PredefinedValueToString(NVDRS_SETTING setting)
        {
            var settingUnion = setting.predefinedValue;
            switch (setting.settingType)
            {
                case NVDRS_SETTING_TYPE.NVDRS_DWORD_TYPE:
                    return settingUnion.dwordValue.ToString();
                case NVDRS_SETTING_TYPE.NVDRS_QWORD_TYPE:
                    return settingUnion.qwordValue.ToString();
                case NVDRS_SETTING_TYPE.NVDRS_STRING_TYPE:
                    return settingUnion.ansiStringValue;
                case NVDRS_SETTING_TYPE.NVDRS_WSTRING_TYPE:
                    return settingUnion.stringValue;
                case NVDRS_SETTING_TYPE.NVDRS_BINARY_TYPE:
                    return Convert.ToBase64String(settingUnion.binaryValue);
                default:
                    return "";
            }
        }

        private static string ConvertSettingValueToString(NVDRS_SETTING setting)
        {
            var settingUnion = setting.currentValue;
            if (setting.isCurrentPredefined == 1)
            {
                settingUnion = setting.predefinedValue;
            }

            switch (setting.settingType)
            {
                case NVDRS_SETTING_TYPE.NVDRS_DWORD_TYPE:
                    return settingUnion.dwordValue.ToString();
                case NVDRS_SETTING_TYPE.NVDRS_QWORD_TYPE:
                    return settingUnion.qwordValue.ToString();
                case NVDRS_SETTING_TYPE.NVDRS_STRING_TYPE:
                    return settingUnion.ansiStringValue;
                case NVDRS_SETTING_TYPE.NVDRS_WSTRING_TYPE:
                    return settingUnion.stringValue;
                case NVDRS_SETTING_TYPE.NVDRS_BINARY_TYPE:
                    return Convert.ToBase64String(settingUnion.binaryValue);
                default:
                    throw new Exception("invalid setting type");
            }
        }

        private static SettingValueType MapValueType(NVDRS_SETTING_TYPE input)
        {
            switch (input)
            {
                case NVDRS_SETTING_TYPE.NVDRS_BINARY_TYPE: return SettingValueType.Binary;
                case NVDRS_SETTING_TYPE.NVDRS_QWORD_TYPE: return SettingValueType.Qword;
                case NVDRS_SETTING_TYPE.NVDRS_STRING_TYPE: return SettingValueType.AnsiString;
                case NVDRS_SETTING_TYPE.NVDRS_WSTRING_TYPE: return SettingValueType.String;
                default: return SettingValueType.Dword;
            }
        }

        public static NVDRS_SETTING ConvertProfileSettingToDrsSetting(ProfileSetting setting)
        {
            var newSetting = new NVDRS_SETTING()
            {
                version = NvapiDrsWrapper.NVDRS_SETTING_VER,
                settingId = setting.SettingId,
                settingType = MapValueType(setting.ValueType),
                settingLocation = NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION,
                currentValue = ConvertStringToSettingUnion(setting.ValueType, setting.SettingValue),
            };
            return newSetting;
        }

        private static NVDRS_SETTING_UNION ConvertStringToSettingUnion(SettingValueType valueType, string valueString)
        {
            var union = new NVDRS_SETTING_UNION();
            switch (valueType)
            {
                case SettingValueType.Dword:
                    union.dwordValue = uint.Parse(valueString);
                    break;
                case SettingValueType.Qword:
                    union.qwordValue = ulong.Parse(valueString);
                    break;
                case SettingValueType.String:
                    union.stringValue = valueString;
                    break;
                case SettingValueType.AnsiString:
                    union.ansiStringValue = valueString;
                    break;
                case SettingValueType.Binary:
                    union.binaryValue = Convert.FromBase64String(valueString);
                    break;
                default:
                    throw new Exception("invalid value type");
            }
            return union;
        }

        private static NVDRS_SETTING_TYPE MapValueType(SettingValueType input)
        {
            switch (input)
            {
                case SettingValueType.Binary: return NVDRS_SETTING_TYPE.NVDRS_BINARY_TYPE;
                case SettingValueType.Qword: return NVDRS_SETTING_TYPE.NVDRS_QWORD_TYPE;
                case SettingValueType.AnsiString: return NVDRS_SETTING_TYPE.NVDRS_STRING_TYPE;
                case SettingValueType.String: return NVDRS_SETTING_TYPE.NVDRS_WSTRING_TYPE;
                default: return NVDRS_SETTING_TYPE.NVDRS_DWORD_TYPE;
            }
        }

    }
}
