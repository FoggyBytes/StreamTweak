// Ported from NVIDIA Profile Inspector (MIT, © Orbmu2k).
//   nvidiaProfileInspector/Common/DrsImportService.cs
// Changes for StreamTweak (functional capture/restore core only):
//   • namespace → StreamTweak.Nvidia.Drs; types from StreamTweak.Nvidia.{Native,Import}.
//   • ctor takes only the decrypter (no meta / settingService / scannerService).
//   • base ctor receives NO decrypter, so base.GetProfileSettings does NOT auto-
//     decrypt; CreateProfileForExport / UpdateSettings decrypt MANUALLY exactly once,
//     exactly as upstream does (avoids double-decryption).
//   • ResetProfile() inlined here (upstream lived in DrsSettingsService) — for the
//     predefined global profile it is just DRS_RestoreProfileDefault + SaveSettings.
//   • The app-in-use scanner hint in the ImportProfiles catch was removed (the global
//     profile has no applications, so that path never fires).
//   • Added ExportGlobalProfile / CreateGlobalProfileExport convenience entry points.
//   • Dropped unused upstream methods (text-file im/export, public MergeProfiles).
//
// Export semantics (includePredefined:false) = only settings present in the profile
// that are NOT at their predefined/default value = the diff-from-default snapshot.
// Import semantics = ResetProfile then re-apply snapshot AND delete any customization
// not in the snapshot = authoritative, exact restore of the captured state.
using StreamTweak.Nvidia.Helper;
using StreamTweak.Nvidia.Import;
using StreamTweak.Nvidia.Native;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using nvw = StreamTweak.Nvidia.Native.NvapiDrsWrapper;

namespace StreamTweak.Nvidia.Drs
{
    public class DrsImportService : DrsSettingsServiceBase
    {
        private readonly DrsDecrypterService _DecrypterService;

        public DrsImportService(DrsDecrypterService decrypterService)
            : base()
        {
            _DecrypterService = decrypterService;
        }

        // ── StreamTweak convenience: global driver profile ──────────────────────

        /// <summary>
        /// Captures the global driver profile (empty profile name → current global)
        /// as a diff-from-default Profile, ready to serialize into a .nip.
        /// </summary>
        public Profile CreateGlobalProfileExport()
        {
            return DrsSession((hSession) =>
                CreateProfileForExport(hSession, "", includePredefined: false));
        }

        /// <summary>Captures the global driver profile and writes it to a .nip file.</summary>
        public void ExportGlobalProfile(string filename)
        {
            var exports = new Profiles { CreateGlobalProfileExport() };
            XMLHelper<Profiles>.SerializeToXmlFile(exports, filename, Encoding.Unicode, true);
        }

        /// <summary>
        /// Reads the global profile's PREDEFINED (driver default) value for every
        /// current-profile setting that has a valid predefined value. Returned as
        /// settingId → default-value-string. Decryption is applied manually (profile
        /// name "" for the global profile), mirroring the export read path so encrypted
        /// "internal" settings yield correct defaults. (StreamTweak addition.)
        /// </summary>
        public Dictionary<uint, string> ReadGlobalDefaults()
        {
            return DrsSession((hSession) =>
            {
                var result = new Dictionary<uint, string>();
                var hProfile = GetProfileHandle(hSession, "");
                if (hProfile == IntPtr.Zero) return result;

                var settings = GetProfileSettings(hSession, hProfile);
                foreach (var setting in settings)
                {
                    if (setting.settingLocation != NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION)
                        continue;
                    if (setting.isPredefinedValid != 1)
                        continue;

                    var s = setting;
                    _DecrypterService.DecryptSettingIfNeeded("", ref s);
                    result[s.settingId] = ImportExportUtil.PredefinedValueToString(s);
                }
                return result;
            });
        }

        // ── Upstream export (kept, faithful) ────────────────────────────────────

        public void ExportAllCustomizedProfiles(string filename)
        {
            var profileNames = GetCustomizedProfileNames();
            ExportProfiles(profileNames, filename, includePredefined: false);
        }

        public void ExportProfiles(List<string> profileNames, string filename, bool includePredefined)
        {
            var exports = new Profiles();

            DrsSession((hSession) =>
            {
                foreach (var profileName in profileNames)
                {
                    var profile = CreateProfileForExport(hSession, profileName, includePredefined);
                    exports.Add(profile);
                }
            });

            XMLHelper<Profiles>.SerializeToXmlFile(exports, filename, Encoding.Unicode, true);
        }

        private List<string> GetCustomizedProfileNames()
        {
            var customizedProfiles = new List<string>();

            DrsSession((hSession) =>
            {
                foreach (var hProfile in EnumProfileHandles(hSession))
                {
                    var profile = GetProfileInfo(hSession, hProfile);
                    if (profile.profileName == null)
                        continue;

                    if (profile.isPredefined == 0)
                    {
                        customizedProfiles.Add(profile.profileName);
                        continue;
                    }

                    var settings = GetProfileSettings(hSession, hProfile);
                    if (settings.Any(_ =>
                        _.settingLocation == NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION &&
                        _.isCurrentPredefined != 1))
                    {
                        customizedProfiles.Add(profile.profileName);
                    }
                }
            });

            return customizedProfiles
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .OrderBy(_ => _, StringComparer.InvariantCultureIgnoreCase)
                .ToList();
        }

        private Profile CreateProfileForExport(IntPtr hSession, string profileName, bool includePredefined)
        {
            var result = new Profile();

            var hProfile = GetProfileHandle(hSession, profileName);
            if (hProfile != IntPtr.Zero)
            {

                result.ProfileName = profileName;

                var apps = GetProfileApplications(hSession, hProfile);
                foreach (var app in apps)
                {
                    result.Executeables.Add(app.appName);
                }

                var settings = GetProfileSettings(hSession, hProfile);
                foreach (var setting in settings)
                {
                    var isPredefined = setting.isCurrentPredefined == 1;
                    var isCurrentProfile = setting.settingLocation ==
                                           NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION;

                    if (isCurrentProfile && (!isPredefined || includePredefined))
                    {
                        var exportSetting = setting;
                        _DecrypterService.DecryptSettingIfNeeded(profileName, ref exportSetting);

                        var profileSetting = ImportExportUtil
                            .ConvertDrsSettingToProfileSetting(exportSetting);

                        result.Settings.Add(profileSetting);
                    }
                }

            }

            return result;
        }

        // ── Import / restore ────────────────────────────────────────────────────

        public string ImportProfiles(string filename)
        {
            return ImportProfiles(new[] { filename });
        }

        public string ImportProfiles(IEnumerable<string> filenames)
        {
            var sbFailedProfilesMessage = new StringBuilder();
            var profiles = LoadAndMergeProfiles(filenames);

            DrsSession((hSession) =>
            {
                foreach (Profile profile in profiles)
                {
                    var profileCreated = false;
                    var hProfile = GetProfileHandle(hSession, profile.ProfileName);
                    if (hProfile == IntPtr.Zero)
                    {
                        hProfile = CreateProfile(hSession, profile.ProfileName);
                        nvw.Instance.DRS_SaveSettings(hSession);
                        profileCreated = true;
                    }

                    if (hProfile != IntPtr.Zero)
                    {
                        ResetProfile(profile.ProfileName);
                        try
                        {
                            UpdateApplications(hSession, hProfile, profile);
                            UpdateSettings(hSession, hProfile, profile, profile.ProfileName);
                        }
                        catch (NvapiException nex)
                        {
                            if (profileCreated)
                            {
                                nvw.Instance.DRS_DeleteProfile(hSession, hProfile);
                            }

                            sbFailedProfilesMessage.AppendLine(string.Format("Failed to import profile '{0}'", profile.ProfileName));
                            var appEx = nex as NvapiAddApplicationException;
                            if (appEx != null)
                            {
                                sbFailedProfilesMessage.AppendLine(string.Format("- application '{0}' is already in use by another profile", appEx.ApplicationName));
                            }
                            else
                            {
                                sbFailedProfilesMessage.AppendLine(string.Format("- {0}", nex.Message));
                            }
                            sbFailedProfilesMessage.AppendLine("");
                        }
                        nvw.Instance.DRS_SaveSettings(hSession);
                    }
                }
            });

            return sbFailedProfilesMessage.ToString();
        }

        /// <summary>
        /// Inlined from upstream DrsSettingsService.ResetProfile (out param dropped).
        /// For the predefined global profile this restores all defaults; for a
        /// user profile it deletes every current-profile setting.
        /// </summary>
        private void ResetProfile(string profileName)
        {
            DrsSession((hSession) =>
            {
                var hProfile = GetProfileHandle(hSession, profileName);
                var profile = GetProfileInfo(hSession, hProfile);

                if (profile.isPredefined == 1)
                {
                    var nvRes = nvw.Instance.DRS_RestoreProfileDefault(hSession, hProfile);
                    if (nvRes != NvAPI_Status.NVAPI_OK)
                        throw new NvapiException("DRS_RestoreProfileDefault", nvRes);

                    SaveSettings(hSession);
                }
                else if (profile.numOfSettings > 0)
                {
                    int dropCount = 0;
                    var settings = GetProfileSettings(hSession, hProfile);

                    foreach (var setting in settings)
                    {
                        if (setting.settingLocation == NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION)
                        {
                            if (nvw.Instance.DRS_DeleteProfileSetting(hSession, hProfile, setting.settingId) == NvAPI_Status.NVAPI_OK)
                            {
                                dropCount++;
                            }
                        }
                    }
                    if (dropCount > 0)
                    {
                        SaveSettings(hSession);
                    }
                }
            });
        }

        private Profiles LoadAndMergeProfiles(IEnumerable<string> filenames)
        {
            var mergedProfiles = new Dictionary<string, Profile>(StringComparer.InvariantCultureIgnoreCase);
            var profileOrder = new List<string>();

            foreach (var filename in (filenames ?? Enumerable.Empty<string>()).Where(_ => !string.IsNullOrWhiteSpace(_)))
            {
                var fileProfiles = XMLHelper<Profiles>.DeserializeFromXMLFile(filename);

                foreach (var profile in fileProfiles)
                {
                    if (!mergedProfiles.TryGetValue(profile.ProfileName, out var mergedProfile))
                    {
                        mergedProfile = new Profile
                        {
                            ProfileName = profile.ProfileName
                        };

                        mergedProfiles.Add(profile.ProfileName, mergedProfile);
                        profileOrder.Add(profile.ProfileName);
                    }

                    MergeExecutables(mergedProfile, profile);
                    MergeSettings(mergedProfile, profile);
                }
            }

            var result = new Profiles();
            foreach (var profileName in profileOrder)
                result.Add(mergedProfiles[profileName]);

            return result;
        }

        private void MergeExecutables(Profile targetProfile, Profile sourceProfile)
        {
            var executableIndexByName = new Dictionary<string, int>(StringComparer.InvariantCultureIgnoreCase);

            for (var index = 0; index < targetProfile.Executeables.Count; index++)
                executableIndexByName[targetProfile.Executeables[index]] = index;

            foreach (var executable in sourceProfile.Executeables)
            {
                if (executableIndexByName.TryGetValue(executable, out var existingIndex))
                {
                    targetProfile.Executeables[existingIndex] = executable;
                }
                else
                {
                    executableIndexByName[executable] = targetProfile.Executeables.Count;
                    targetProfile.Executeables.Add(executable);
                }
            }
        }

        private void MergeSettings(Profile targetProfile, Profile sourceProfile)
        {
            var settingIndexById = new Dictionary<uint, int>();

            for (var index = 0; index < targetProfile.Settings.Count; index++)
                settingIndexById[targetProfile.Settings[index].SettingId] = index;

            foreach (var setting in sourceProfile.Settings)
            {
                var clonedSetting = CloneSetting(setting);

                if (settingIndexById.TryGetValue(clonedSetting.SettingId, out var existingIndex))
                {
                    targetProfile.Settings[existingIndex] = clonedSetting;
                }
                else
                {
                    settingIndexById[clonedSetting.SettingId] = targetProfile.Settings.Count;
                    targetProfile.Settings.Add(clonedSetting);
                }
            }
        }

        private ProfileSetting CloneSetting(ProfileSetting setting)
        {
            return new ProfileSetting
            {
                SettingId = setting.SettingId,
                SettingNameInfo = setting.SettingNameInfo,
                SettingValue = setting.SettingValue,
                ValueType = setting.ValueType
            };
        }

        private bool ExistsImportApp(string appName, Profile importProfile)
        {
            return importProfile.Executeables.Any(x => x.Equals(appName));
        }

        private void UpdateApplications(IntPtr hSession, IntPtr hProfile, Profile importProfile)
        {
            var alreadySet = new HashSet<string>();

            var apps = GetProfileApplications(hSession, hProfile);
            foreach (var app in apps)
            {
                if (ExistsImportApp(app.appName, importProfile) && !alreadySet.Contains(app.appName))
                    alreadySet.Add(app.appName);
                else
                    nvw.Instance.DRS_DeleteApplication(hSession, hProfile, new StringBuilder(app.appName));
            }

            foreach (string appName in importProfile.Executeables)
            {
                if (!alreadySet.Contains(appName))
                {
                    try
                    {
                        AddApplication(hSession, hProfile, appName);
                    }
                    catch (NvapiException)
                    {
                        throw new NvapiAddApplicationException(appName);
                    }
                }
            }
        }

        private ProfileSetting GetImportProfileSetting(uint settingId, Profile importProfile)
        {
            return importProfile.Settings
                .FirstOrDefault(x => x.SettingId.Equals(settingId));
        }

        private bool ExistsImportValue(uint settingId, Profile importProfile)
        {
            return importProfile.Settings
                .Any(x => x.SettingId.Equals(settingId));
        }

        private void UpdateSettings(IntPtr hSession, IntPtr hProfile, Profile importProfile, string profileName)
        {
            var alreadySet = new HashSet<uint>();

            var settings = GetProfileSettings(hSession, hProfile);
            foreach (var setting in settings)
            {
                var isCurrentProfile = setting.settingLocation == NVDRS_SETTING_LOCATION.NVDRS_CURRENT_PROFILE_LOCATION;
                var isPredefined = setting.isCurrentPredefined == 1;

                if (isCurrentProfile)
                {
                    bool exitsValueInImport = ExistsImportValue(setting.settingId, importProfile);
                    var importSetting = GetImportProfileSetting(setting.settingId, importProfile);

                    var decryptedSetting = setting;
                    _DecrypterService.DecryptSettingIfNeeded(profileName, ref decryptedSetting);

                    if (isPredefined && exitsValueInImport && ImportExportUtil.AreDrsSettingEqualToProfileSetting(decryptedSetting, importSetting))
                    {
                        alreadySet.Add(setting.settingId);
                    }
                    else if (exitsValueInImport)
                    {
                        var updatedSetting = ImportExportUtil.ConvertProfileSettingToDrsSetting(importSetting);
                        StoreSetting(hSession, hProfile, updatedSetting);
                        alreadySet.Add(setting.settingId);
                    }
                    else if (!isPredefined)
                    {
                        nvw.Instance.DRS_DeleteProfileSetting(hSession, hProfile, setting.settingId);
                    }
                }
            }

            foreach (var setting in importProfile.Settings)
            {
                if (!alreadySet.Contains(setting.SettingId))
                {
                    var newSetting = ImportExportUtil.ConvertProfileSettingToDrsSetting(setting);
                    try
                    {
                        StoreSetting(hSession, hProfile, newSetting);
                    }
                    catch (NvapiException ex)
                    {
                        if (ex.Status != NvAPI_Status.NVAPI_SETTING_NOT_FOUND)
                            throw;
                    }
                }
            }
        }

    }
}
