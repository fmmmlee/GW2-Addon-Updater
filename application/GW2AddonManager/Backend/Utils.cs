using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Abstractions;
using System.IO.Compression;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace GW2AddonManager
{
    struct SemanticVersion
    {
        public string Name;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint PatchVersion;

        public override string ToString()
        {
            return $"{MajorVersion}.{MinorVersion}.{PatchVersion}";
        }
    }

    public delegate void UpdateMessageChangedEventHandler(object sender, string message);
    public delegate void UpdateProgressChangedEventHandler(object sender, int pct);

    public interface IUpdateChangedEvents
    {
        public event UpdateMessageChangedEventHandler MessageChanged;
        public event UpdateProgressChangedEventHandler ProgressChanged;
    }

    public abstract class UpdateChangedEvents : IUpdateChangedEvents, IProgress<float>
    {
        public event UpdateMessageChangedEventHandler MessageChanged;
        public event UpdateProgressChangedEventHandler ProgressChanged;

        protected void OnProgressChanged(int i, int n)
        {
            ProgressChanged?.Invoke(this, i * 100 / n);
        }

        protected void OnMessageChanged(string msg)
        {
            MessageChanged?.Invoke(this, msg);
        }

        public void Report(float value) => OnProgressChanged((int)(value * 100), 100);
    }

    public static class Constants
    {
        public const string AddonFolder = "resources\\addons";
    }

    public interface IHyperlinkHandler
    {

    }

    public static class Utils
    {
        public static void Hyperlink_RequestNavigate(this IHyperlinkHandler win, object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            _ = Process.Start(new ProcessStartInfo {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true
            });
            e.Handled = true;
        }

        public static void RemoveFiles(IFileSystem fs, IEnumerable<string> files, string basePath = "")
        {
            foreach (var f in files) {
                var fp = fs.Path.Combine(basePath, f);
                if (fs.File.Exists(fp))
                    fs.File.Delete(fp);
            }
        }

        public static void DeleteIfExists(this IFile f, string path)
        {
            if(f.Exists(path))
                f.Delete(path);
        }

        public static List<string> ExtractArchiveWithFilesList(string archiveFilePath, string destFolder, IFileSystem fs)
        {
            var folderName = fs.Path.Combine(fs.Path.GetTempPath(), fs.Path.GetFileNameWithoutExtension(archiveFilePath));
            if (fs.Directory.Exists(folderName))
                fs.Directory.Delete(folderName, true);

            ZipFile.ExtractToDirectory(archiveFilePath, folderName);

            if(fs.Directory.GetFiles(folderName).Length == 0 && fs.Directory.GetDirectories(folderName).Length == 1)
                folderName = fs.Directory.GetDirectories(folderName)[0];

            var absoluteFiles = new List<string>();
            foreach (var f in fs.Directory.EnumerateFiles(folderName, "*", SearchOption.AllDirectories))
                absoluteFiles.Add(f);

            var relFiles = new List<string>();
            foreach (var f in absoluteFiles) {
                var relFile = fs.Path.GetRelativePath(folderName, f);
                relFiles.Add(relFile);
                _ = fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(fs.Path.Combine(destFolder, relFile)));
                fs.File.Copy(f, fs.Path.Combine(destFolder, relFile));
            }

            return relFiles;
        }

        public static string GetRelativePath(this IPath _, string relativeTo, string path)
        {
            return Path.GetRelativePath(relativeTo, path);
        }
    }

    public class ArrayMultiValueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            return values.Clone();
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            return (object[])value;
        }
    }

    public class BoolVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return (bool)value ? Visibility.Visible : (object)Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }
}
