using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace GW2AddonManager
{
    public interface ILoaderManager : IUpdateChangedEvents
    {
        Task Update();
        void Uninstall();
    }

    public class LoaderManager : UpdateChangedEvents, ILoaderManager
    {
        private class LoaderDLL : IDisposable
        {
            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct GW2Load_LoaderVersion
            {
                public uint descriptionVersion;
                public uint majorAddonVersion;
                public uint minorAddonVersion;
                public uint patchAddonVersion;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct GW2Load_AddonDescription
            {
                public uint descriptionVersion;
                public uint majorAddonVersion;
                public uint minorAddonVersion;
                public uint patchAddonVersion;
                [MarshalAs(UnmanagedType.LPStr)]
                public string name;
            }

            [StructLayout(LayoutKind.Sequential, Pack = 4)]
            struct GW2Load_EnumeratedAddon
            {
                [MarshalAs(UnmanagedType.LPStr)]
                public string path;
                public GW2Load_AddonDescription description;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate IntPtr GW2Load_GetLoaderVersion();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            delegate IntPtr GW2Load_GetAddonsInDirectory([MarshalAs(UnmanagedType.LPStr)] string directory, ref uint count);


            IntPtr _library;
            GW2Load_GetLoaderVersion _getLoaderVersion;
            GW2Load_GetAddonsInDirectory _getAddonsInDirectory;
            bool disposedValue_;

            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            static extern IntPtr LoadLibrary(string libraryName);

            [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            static extern bool FreeLibrary(IntPtr library);

            [DllImport("kernel32", CharSet = CharSet.Ansi, SetLastError = true)]
            [return: MarshalAs(UnmanagedType.FunctionPtr)]
            static extern IntPtr GetProcAddress(IntPtr library, string name);

            private LoaderDLL(IntPtr library)
            {
                _library = library;
                _getLoaderVersion = Marshal.GetDelegateForFunctionPointer<GW2Load_GetLoaderVersion>(GetProcAddress(_library, "GW2Load_GetLoaderVersion"));
                _getAddonsInDirectory = Marshal.GetDelegateForFunctionPointer<GW2Load_GetAddonsInDirectory>(GetProcAddress(_library, "GW2Load_GetAddonsInDirectory"));
            }

            public static LoaderDLL? FromPath(IFileSystem fs, string path)
            {
                if (fs.File.Exists(path))
                {
                    return new LoaderDLL(LoadLibrary(path));
                }
                else
                    return null;
            }

            public SemanticVersion GetLoaderVersion()
            {
                var ver = Marshal.PtrToStructure<GW2Load_LoaderVersion>(_getLoaderVersion());
                return new SemanticVersion
                {
                    Name = "GW2Load",
                    MajorVersion = ver.majorAddonVersion,
                    MinorVersion = ver.minorAddonVersion,
                    PatchVersion = ver.patchAddonVersion
                };
            }

            public IList<(string Path, SemanticVersion Desc)> GetAddonsInDirectory(string directory)
            {
                uint count = 0;
                var unmanagedArray = _getAddonsInDirectory(directory, ref count);
                var list = new List<(string Path, SemanticVersion Desc)>();
                list.Capacity = (int)count;

                for(uint i = 0; i < count; ++i)
                {
                    var enumeratedAddon = Marshal.PtrToStructure<GW2Load_EnumeratedAddon>(
                        new IntPtr(unmanagedArray.ToInt64() + i * Marshal.SizeOf<GW2Load_EnumeratedAddon>()));

                    list.Add((enumeratedAddon.path, new SemanticVersion
                    {
                        Name = enumeratedAddon.description.name,
                        MajorVersion = enumeratedAddon.description.majorAddonVersion,
                        MinorVersion = enumeratedAddon.description.minorAddonVersion,
                        PatchVersion = enumeratedAddon.description.patchAddonVersion
                    }));
                }

                return list;
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!disposedValue_)
                {
                    if (disposing)
                    {
                        _ = FreeLibrary(_library);
                    }

                    disposedValue_ = true;
                }
            }

            ~LoaderDLL()
            {
                // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
                Dispose(disposing: false);
            }

            public void Dispose()
            {
                // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
                Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }

        private readonly IConfigurationProvider _configurationProvider;
        private readonly IAddonRepository _addonRepository;
        private readonly IAddonManager _addonManager;
        private readonly IFileSystem _fileSystem;
        private readonly IHttpClientProvider _httpClientProvider;
        SemanticVersion _loaderVersion;

        public string InstallPath => _configurationProvider.UserConfig.GamePath;
        private string DownloadPath => _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "addon-loader.zip");

        public string LoaderPath => Path.Combine(InstallPath, "msimg32.dll");
        public string LocalLoaderPath => Path.Combine(Assembly.GetExecutingAssembly().Location, "gw2load.dll");

        public LoaderManager(IConfigurationProvider configurationProvider, IAddonRepository addonRepository, IAddonManager addonManager, IFileSystem fileSystem, IHttpClientProvider httpClientProvider, ICoreManager coreManager)
        {
            _configurationProvider = configurationProvider;
            _addonRepository = addonRepository;
            _addonManager = addonManager;
            _fileSystem = fileSystem;
            _httpClientProvider = httpClientProvider;
            coreManager.Uninstalling += (_, _) => Uninstall();

            FetchLoaderVersion().RunSynchronously();
        }

        private async Task<LoaderDLL?> GetLoaderDLL()
        {
            var dll = LoaderDLL.FromPath(_fileSystem, LocalLoaderPath);
            if(dll == null)
            {
                await Update();
                dll = LoaderDLL.FromPath(_fileSystem, LocalLoaderPath);
            }

            return dll;
        }

        private async Task FetchLoaderVersion()
        {
            using (var dll = await GetLoaderDLL())
            {
                if (dll == null)
                {
                    throw new FileNotFoundException("Could not find addon loader!");
                }
                else
                {
                    _loaderVersion = dll.GetLoaderVersion();
                }
            }
        }

        public async Task Update()
        {
            await _addonManager.Install(new List<AddonInfo> { _addonRepository.Loader.Wrapper });

            if (_addonRepository.Loader.VersionId == _loaderVersion.ToString())
                return;

            OnMessageChanged("Downloading Addon Loader");

            var fileName = DownloadPath;

            if (_fileSystem.File.Exists(fileName))
                _fileSystem.File.Delete(fileName);

            using (var fs = new FileStream(fileName, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await _httpClientProvider.Client.DownloadAsync(_addonRepository.Loader.DownloadUrl, fs, this);
            }

            Install(fileName);
        }

        private void Install(string fileName)
        {
            OnMessageChanged("Installing Addon Loader");

            string[] removeFiles =
            {
                LoaderPath,
                LocalLoaderPath
            };
            Utils.RemoveFiles(_fileSystem, removeFiles);

            _ = Utils.ExtractArchiveWithFilesList(fileName, InstallPath, _fileSystem);
            _fileSystem.File.Copy(removeFiles[0], removeFiles[1]);
        }

        public void Uninstall()
        {
            _fileSystem.File.DeleteIfExists(LoaderPath);
            _fileSystem.File.DeleteIfExists(LocalLoaderPath);
        }
    }
}
