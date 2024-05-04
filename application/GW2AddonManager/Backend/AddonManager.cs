using GW2AddonManager.Localization;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.IO;
using System;
using System.Threading.Tasks;
using System.IO.Abstractions;

namespace GW2AddonManager
{
    public interface IAddonManager : IUpdateChangedEvents
    {
        Task Delete(IEnumerable<AddonInfo> addons);
        Task Disable(IEnumerable<AddonInfo> addons);
        Task Enable(IEnumerable<AddonInfo> addons);
        Task Install(IEnumerable<AddonInfo> addons);

        string AddonsPath { get; }
    }

    public class AddonManager : UpdateChangedEvents, IAddonManager
    {
        private const string ArcDPSFolder = "arcdps";
        private const string DisabledExtension = ".dll.disabled";
        private const string EnabledExtension = ".dll";

        private readonly IConfigurationProvider _configurationProvider;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientProvider _httpClientProvider;
        private readonly ICoreManager _coreManager;
        
        public string AddonsPath => _fileSystem.Path.Combine(_configurationProvider.UserConfig.GamePath, "addons");

        public AddonManager(IConfigurationProvider configurationProvider, IFileSystem fileSystem, IHttpClientProvider httpClientProvider, ICoreManager coreManager)
        {
            _configurationProvider = configurationProvider;
            _fileSystem = fileSystem;
            _httpClientProvider = httpClientProvider;
            _coreManager = coreManager;
            coreManager.Uninstalling += (_, _) => Uninstall();
        }

        private string GetAddonInstallPath(AddonInfo addon)
        {
            return _fileSystem.Path.Combine(AddonsPath, addon.InstallMode == InstallMode.Binary ? addon.Nickname : ArcDPSFolder);
        }

        private string? GetAddonBinaryPath(AddonInfo addon, AddonState state)
        {
            var folderPath = GetAddonInstallPath(addon);
            var extension = state.Disabled ? DisabledExtension : EnabledExtension;
            if(addon.InstallMode == InstallMode.Binary)
                return _fileSystem.Path.Combine(folderPath, addon.Nickname + extension);
            else if(addon.InstallMode == InstallMode.ArcDPSAddon) {
                if(addon.PluginName != null)
                    return _fileSystem.Path.Combine(folderPath, addon.PluginName + extension);
                else {
                    var files = _fileSystem.Directory.GetFiles(folderPath, addon.PluginNamePattern + extension);
                    if(files.Length > 0)
                        return files[0];
                    else
                        return null;
                }
            }
            else
                return null;
        }

        private void DisableEnable(AddonInfo addon, Dictionary<string, AddonState> states, bool enable)
        {
            var state = states[addon.Nickname];
            if (state.Disabled == !enable)
            {
                _coreManager.AddLog($"Skipping {addon.AddonName}, not installed or already in desired state.");
                return;
            }

            var path = GetAddonBinaryPath(addon, state);
            if(_fileSystem.File.Exists(path)) {
                var newPath = _fileSystem.Path.ChangeExtension(path, enable ? EnabledExtension : DisabledExtension);
                _fileSystem.File.Move(path, newPath);
            }
            else
            {
                _coreManager.AddLog($"Could not {(enable ? "enable" : "disable")} {addon.AddonName}, expected addon path '{path}' does not exist!");
                return;
            }

            _coreManager.AddLog($"{(enable ? "Enabled" : "Disabled")} {addon.AddonName}.");

            states[addon.Nickname] = state with
            {
                Disabled = !enable
            };
        }

        private void Delete(AddonInfo addon, Dictionary<string, AddonState> states)
        {
            var state = states[addon.Nickname];

            _coreManager.AddLog($"Deleting {addon.AddonName}...");

            var folderPath = GetAddonInstallPath(addon);

            if(_fileSystem.Directory.Exists(folderPath))
            {
                var dllPath = GetAddonBinaryPath(addon, state) ?? throw new ArgumentException($"Could not determine install path for {addon.AddonName}!");

                var files = new List<string>
                {
                    dllPath
                };
                foreach (var f in addon.Files)
                    files.Add(_fileSystem.Path.Combine(folderPath, f));

                foreach (var f in files) {
                    if (_fileSystem.File.Exists(f))
                    {
                        _coreManager.AddLog($"Deleting file '{f}'...");
                        _fileSystem.File.Delete(f);
                    }
                }
            } else
                _coreManager.AddLog($"Directory '{folderPath}' does not exist, addon does not appear to be installed?");

            _ = states.Remove(addon.Nickname);

            _coreManager.AddLog($"Deleted {addon.AddonName}.");
        }

        public async Task Delete(IEnumerable<AddonInfo> addons)
        {
            if(addons == null)
            {
                _ = Popup.Show(StaticText.NoAddonsSelected, StaticText.CannotProceedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (Popup.Show(StaticText.DeleteAddonsPrompt, StaticText.DeleteTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) {
                int i = 0;
                int n = addons.Count();
                OnProgressChanged(i, n);

                var states = new Dictionary<string, AddonState>(_configurationProvider.UserConfig.AddonsState);
                try {
                    foreach (AddonInfo addon in addons) {
                        OnMessageChanged($"Deleting {addon.AddonName}...");
                        Delete(addon, states);
                        OnProgressChanged(++i, n);
                    }
                }
                catch (Exception ex)
                {
                    _coreManager.AddLog($"Exception while deleting addons ({string.Join(", ", addons.Select(x => x.AddonName))}): {ex.Message}");

                    _ = Popup.Show("Error while deleting some addons: " + ex.Message, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                _configurationProvider.UserConfig = _configurationProvider.UserConfig with { AddonsState = states };
            }
        }

        /* 
         * Credit: Fidel @ StackOverflow
         * Modified version of their answer at https://stackoverflow.com/a/54616044/9170673
         */
        private string GetFilenameFromUrl(string url)
        {
            if(url.EndsWith(".zip") || url.EndsWith(".dll"))
                return url[(url.LastIndexOf('/') + 1)..];

            string result = "";

            var req = System.Net.WebRequest.Create(url);
            req.Method = "GET";
            using (System.Net.WebResponse resp = req.GetResponse()) {
                result = _fileSystem.Path.GetFileName(resp.ResponseUri.AbsoluteUri);
            }
            return result[(result.LastIndexOf('/') + 1)..];
        }

        private async Task Install(AddonInfo addon, Dictionary<string, AddonState> states)
        {
            if(!states.TryGetValue(addon.Nickname, out var state))
                state = AddonState.Default(addon.Nickname);

            if (state.Installed && (state.VersionId == addon.VersionId || addon.SelfUpdate))
            {
                _coreManager.AddLog($"Skipping {addon.AddonName}, already installed and at the right version or self-updating.");
                return;
            }

            _coreManager.AddLog($"Installing {addon.AddonName}...");

            var url = addon.DownloadUrl;
            var baseFolder = Path.GetTempPath();
            var destFolder = FolderPath(addon);
            var fileName = Path.Combine(baseFolder, GetFilenameFromUrl(url));

            if (_fileSystem.File.Exists(fileName))
                _fileSystem.File.Delete(fileName);

            using(var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _httpClientProvider.Client.DownloadAsync(url, fs, this);
            }

            _coreManager.AddLog($"Downloaded {addon.AddonName}.");

            if (state.Installed)
            {
                Utils.RemoveFiles(_fileSystem, state.InstalledFiles, destFolder);
                _coreManager.AddLog($"Removed existing installation of {addon.AddonName}.");
            }

            List<string> relFiles;

            if (addon.DownloadType == DownloadType.Archive)
                relFiles = Utils.ExtractArchiveWithFilesList(fileName, destFolder, _fileSystem);
            else
            {
                var relFile = _fileSystem.Path.GetRelativePath(baseFolder, fileName);
                _ = _fileSystem.Directory.CreateDirectory(destFolder);
                _fileSystem.File.Copy(fileName, _fileSystem.Path.Combine(destFolder, relFile), true);

                relFiles = new List<string> { relFile };
            }

            states[addon.Nickname] = state with
            {
                Installed = true,
                VersionId = addon.VersionId,
                InstalledFiles = relFiles
            };

            _fileSystem.File.Delete(fileName);

            _coreManager.AddLog($"Installed {addon.AddonName}.");
        }

        public async Task Install(IEnumerable<AddonInfo> addons)
        {
            if (addons == null)
            {
                _ = Popup.Show(StaticText.NoAddonsSelected, StaticText.CannotProceedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int i = 0;
            int n = addons.Count();
            OnProgressChanged(i, n);

            var states = new Dictionary<string, AddonState>(_configurationProvider.UserConfig.AddonsState);
            try {
                foreach (AddonInfo addon in addons) {
                    OnMessageChanged($"Installing {addon.AddonName}...");
                    await Install(addon, states);
                    OnProgressChanged(++i, n);
                }
            }
            catch (Exception ex)
            {
                _coreManager.AddLog($"Exception while installing addons ({string.Join(", ", addons.Select(x => x.AddonName))}): {ex.Message}");
                _ = Popup.Show("Error while installing some addons: " + ex.Message, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            _configurationProvider.UserConfig = _configurationProvider.UserConfig with { AddonsState = states };
        }

        private void DisableEnable(bool enable, IEnumerable<AddonInfo> addons)
        {
            int i = 0;
            int n = addons.Count();
            OnProgressChanged(i, n);

            var states = new Dictionary<string, AddonState>(_configurationProvider.UserConfig.AddonsState);
            try {
                foreach (AddonInfo addon in addons) {
                    OnMessageChanged($"{(enable ? "Enabling" : "Disabling")} {addon.AddonName}...");
                    DisableEnable(addon, states, enable);
                    OnProgressChanged(++i, n);
                }
            }
            catch (Exception ex)
            {
                _coreManager.AddLog($"Exception while {(enable ? "enabling" : "disabling")} addons ({string.Join(", ", addons.Select(x => x.AddonName))}): {ex.Message}");
                _ = Popup.Show($"Error while {(enable ? "enabling" : "disabling")} some addons: " + ex.Message, "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public Task Disable(IEnumerable<AddonInfo> addons)
        {
            if (addons == null)
            {
                _ = Popup.Show(StaticText.NoAddonsSelected, StaticText.CannotProceedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }

            if (Popup.Show(StaticText.DisableAddonsPrompt, StaticText.DisableTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) {
                DisableEnable(false, addons);
            }

            return Task.CompletedTask;
        }

        public Task Enable(IEnumerable<AddonInfo> addons)
        {
            if (addons == null)
            {
                _ = Popup.Show(StaticText.NoAddonsSelected, StaticText.CannotProceedTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.CompletedTask;
            }

            if (Popup.Show(StaticText.EnableAddonsPrompt, StaticText.EnableTitle, MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes) {
                DisableEnable(true, addons);
            }

            return Task.CompletedTask;
        }

        public void Uninstall()
        {
            if (_fileSystem.Directory.Exists(AddonsFolder))
                _fileSystem.Directory.Delete(AddonsFolder, true);
        }
    }
}
